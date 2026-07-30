using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace ImmichPeg.Services;

public class FlickrClient
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly string? _accessToken;
    private readonly string? _accessTokenSecret;

    private const string BaseUrl = "https://www.flickr.com/services";
    private const string UploadUrl = "https://api.flickr.com/services/upload/";

    public FlickrClient(string apiKey, string apiSecret, string? accessToken = null, string? accessTokenSecret = null)
    {
        _apiKey = apiKey;
        _apiSecret = apiSecret;
        _accessToken = accessToken;
        _accessTokenSecret = accessTokenSecret;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public bool HasAccessToken => !string.IsNullOrEmpty(_accessToken) && !string.IsNullOrEmpty(_accessTokenSecret);

    // ── OAuth 1.0a ────────────────────────────────────────────────

    public (string Url, string RequestTokenSecret) GetAuthorizationUrl(string callbackUrl)
    {
        var oauthCallback = callbackUrl; // Sign method handles encoding
        var sig = Sign("GET", $"{BaseUrl}/oauth/request_token", new() { ["oauth_callback"] = oauthCallback }, null, null);
        var resp = _http.GetStringAsync($"{BaseUrl}/oauth/request_token?{sig}").Result;
        var parsed = HttpUtility.ParseQueryString(resp);
        var oauthToken = parsed["oauth_token"]!;
        var oauthTokenSecret = parsed["oauth_token_secret"]!;
        return ($"{BaseUrl}/oauth/authorize?oauth_token={oauthToken}&perms=write", oauthTokenSecret);
    }

    public (string AccessToken, string AccessTokenSecret, string UserId, string Username) CompleteAuthorization(
        string oauthToken, string oauthVerifier, string requestTokenSecret)
    {
        var sig = Sign("GET", $"{BaseUrl}/oauth/access_token", new()
        {
            ["oauth_token"] = oauthToken,
            ["oauth_verifier"] = oauthVerifier
        }, oauthToken, requestTokenSecret);
        var resp = _http.GetStringAsync($"{BaseUrl}/oauth/access_token?{sig}").Result;
        var parsed = HttpUtility.ParseQueryString(resp);
        return (
            parsed["oauth_token"]!,
            parsed["oauth_token_secret"]!,
            parsed["user_nsid"]!,
            parsed["username"] ?? parsed["fullname"] ?? parsed["user_nsid"]!
        );
    }

    // ── Upload ────────────────────────────────────────────────────

    public async Task<string> UploadPhotoAsync(byte[] imageData, string filename, string title, string? description = null)
    {
        using var content = new MultipartFormDataContent();

        // Add photo
        var imageContent = new ByteArrayContent(imageData);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(imageContent, "photo", filename);

        // All params to sign (everything except photo)
        var oauthParams = new Dictionary<string, string>
        {
            ["oauth_token"] = _accessToken!,
            ["title"] = title,
            ["description"] = description ?? "",
            ["is_public"] = "0",
            ["is_friend"] = "0",
            ["is_family"] = "0"
        };

        // Sign and add OAuth params
        var sig = Sign("POST", UploadUrl, oauthParams, _accessToken, _accessTokenSecret);
        var sigParams = HttpUtility.ParseQueryString(sig);
        foreach (var key in sigParams.AllKeys)
            oauthParams[key!] = sigParams[key!]!;

        // Add ALL params as form fields (not URL query params)
        foreach (var kv in oauthParams)
            content.Add(new StringContent(kv.Value), kv.Key);

        var resp = await _http.PostAsync(UploadUrl, content);
        var xml = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode || !xml.Contains("<photoid>"))
        {
            var errStart = xml.IndexOf("<err ");
            var errMsg = errStart >= 0 ? xml[errStart..xml.IndexOf("/>", errStart)] : $"HTTP {resp.StatusCode}";
            throw new Exception($"Flickr upload rejected: {errMsg}");
        }
        var idStart = xml.IndexOf("<photoid>") + 9;
        var idEnd = xml.IndexOf("</photoid>");
        return xml[idStart..idEnd];
    }

    // ── Photosets (Albums) ────────────────────────────────────────

    public async Task<string?> FindPhotosetByTitleAsync(string title)
    {
        var sig = Sign("GET", $"{BaseUrl}/rest/", new()
        {
            ["method"] = "flickr.photosets.getList",
            ["api_key"] = _apiKey,
            ["oauth_token"] = _accessToken!
        }, _accessToken, _accessTokenSecret);
        var url = $"{BaseUrl}/rest/?{sig}";
        var resp = await _http.GetStringAsync(url);
        if (resp.Contains("stat=\"fail\""))
        {
            var errMatch = System.Text.RegularExpressions.Regex.Match(resp, "msg=\"([^\"]+)\"");
            var errMsg = errMatch.Success ? errMatch.Groups[1].Value : resp;
            Console.WriteLine($"[FlickrFindPhotoset] Error: {errMsg}");
            return null;
        }
        // Parse XML directly for photoset list
        var psMatches = System.Text.RegularExpressions.Regex.Matches(resp, @"<photoset\s+id=\"([^\"]+)\"[^>]*>\s*<title>([^<]+)</title>");
        foreach (System.Text.RegularExpressions.Match m in psMatches)
        {
            if (m.Groups[2].Value.Trim() == title)
                return m.Groups[1].Value;
        }
        return null;
    }

    public async Task<string> CreatePhotosetAsync(string title, string primaryPhotoId)
    {
        var sig = Sign("GET", $"{BaseUrl}/rest/", new()
        {
            ["method"] = "flickr.photosets.create",
            ["api_key"] = _apiKey,
            ["oauth_token"] = _accessToken!,
            ["title"] = title,
            ["primary_photo_id"] = primaryPhotoId
        }, _accessToken, _accessTokenSecret);
        var url = $"{BaseUrl}/rest/?{sig}";
        var resp = await _http.GetStringAsync(url);
        if (resp.Contains("stat=\"fail\""))
        {
            var errMatch = System.Text.RegularExpressions.Regex.Match(resp, "msg=\"([^\"]+)\"");
            var errMsg = errMatch.Success ? errMatch.Groups[1].Value : resp;
            throw new Exception($"Flickr CreatePhotoset failed: {errMsg}");
        }
        // Extract photoset id directly from XML
        var idMatch = System.Text.RegularExpressions.Regex.Match(resp, @"photoset\s+id=\"([^\"]+)\"");
        return idMatch.Groups[1].Value;
    }

    public async Task AddPhotoToPhotosetAsync(string photosetId, string photoId)
    {
        var sig = Sign("GET", $"{BaseUrl}/rest/", new()
        {
            ["method"] = "flickr.photosets.addPhoto",
            ["api_key"] = _apiKey,
            ["oauth_token"] = _accessToken!,
            ["photoset_id"] = photosetId,
            ["photo_id"] = photoId
        }, _accessToken, _accessTokenSecret);
        await _http.GetStringAsync($"{BaseUrl}/rest/?{sig}");
    }

    public async Task<HashSet<string>> GetPhotosetPhotoIdsAsync(string photosetId)
    {
        var ids = new HashSet<string>();
        int page = 1;
        while (true)
        {
            var sig = Sign("GET", $"{BaseUrl}/rest/", new()
            {
                ["method"] = "flickr.photosets.getPhotos",
                ["api_key"] = _apiKey,
                ["oauth_token"] = _accessToken!,
                ["photoset_id"] = photosetId,
                ["page"] = page.ToString(),
                ["per_page"] = "500"
            }, _accessToken, _accessTokenSecret);
            var resp = await _http.GetStringAsync($"{BaseUrl}/rest/?{sig}");
            // Parse photo IDs directly from XML
            var photoMatches = System.Text.RegularExpressions.Regex.Matches(resp, @"<photo\s+id=\"([^\"]+)\"");
            foreach (System.Text.RegularExpressions.Match m in photoMatches)
                ids.Add(m.Groups[1].Value);
            var pageMatch = System.Text.RegularExpressions.Regex.Match(resp, @"pages=\"(\d+)\"");
            var totalPages = pageMatch.Success ? int.Parse(pageMatch.Groups[1].Value) : 1;
            if (totalPages <= page) break;
            page++;
        }
        return ids;
    }

    // ── Ping ──────────────────────────────────────────────────────

    public async Task<bool> PingAsync()
    {
        try
        {
            if (!HasAccessToken) return false;
            var sig = Sign("GET", $"{BaseUrl}/rest/", new()
            {
                ["method"] = "flickr.test.login",
                ["api_key"] = _apiKey,
                ["oauth_token"] = _accessToken!
            }, _accessToken, _accessTokenSecret);
            var resp = await _http.GetStringAsync($"{BaseUrl}/rest/?{sig}");
            return resp.Contains("stat=\"ok\"");
        }
        catch { return false; }
    }

    // ── OAuth signing ─────────────────────────────────────────────

    private string Sign(string method, string url, Dictionary<string, string> parameters,
        string? token, string? tokenSecret, bool includeApiKey = true)
    {
        var oauth = new Dictionary<string, string>
        {
            ["oauth_consumer_key"] = _apiKey,
            ["oauth_nonce"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            ["oauth_signature_method"] = "HMAC-SHA1",
            ["oauth_timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            ["oauth_version"] = "1.0"
        };
        if (token != null) oauth["oauth_token"] = token;

        var allParams = new Dictionary<string, string>(parameters);
        foreach (var kv in oauth) allParams[kv.Key] = kv.Value;

        var baseString = string.Join("&",
            method.ToUpper(),
            Uri.EscapeDataString(url.Split('?')[0]),
            Uri.EscapeDataString(string.Join("&", allParams
                .OrderBy(k => k.Key).ThenBy(k => k.Value)
                .Select(k => $"{Uri.EscapeDataString(k.Key)}={Uri.EscapeDataString(k.Value)}"))));

        var signingKey = $"{Uri.EscapeDataString(_apiSecret)}&{Uri.EscapeDataString(tokenSecret ?? "")}";
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(signingKey));
        oauth["oauth_signature"] = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(baseString)));
        allParams["oauth_signature"] = oauth["oauth_signature"];

        return string.Join("&", allParams.OrderBy(k => k.Key)
            .Select(k => $"{Uri.EscapeDataString(k.Key)}={Uri.EscapeDataString(k.Value)}"));
    }

    // ── Simple XML→JSON converter for Flickr responses ────────────

    private static string FlickrXmlToJson(string xml)
    {
        var sb = new StringBuilder();
        var tagStack = new Stack<string>();
        var attrStack = new Stack<bool>(); // tracks if current object has had first property
        attrStack.Push(true); // root level
        sb.Append('{');
        int i = 0;
        while (i < xml.Length)
        {
            if (xml[i] == '<')
            {
                // Check for closing tag
                if (i + 1 < xml.Length && xml[i + 1] == '/')
                {
                    var end = xml.IndexOf('>', i);
                    var tag = xml[(i + 2)..end].Trim();
                    i = end + 1;
                    if (tagStack.Count > 0 && tagStack.Peek() == tag)
                    {
                        tagStack.Pop();
                        attrStack.Pop();
                        sb.Append('}');
                    }
                }
                // Skip XML declaration / processing instruction
                else if (i + 1 < xml.Length && xml[i + 1] == '?')
                {
                    var end = xml.IndexOf("?>", i);
                    i = (end >= 0 ? end : i + 1) + 2;
                }
                // Opening tag
                else
                {
                    var end = xml.IndexOf('>', i);
                    var tagContent = xml[(i + 1)..end];
                    var spaceIdx = tagContent.IndexOf(' ');
                    var tagName = spaceIdx > 0 ? tagContent[..spaceIdx] : tagContent;
                    
                    // Check if self-closing
                    bool selfClose = tagContent.EndsWith("/");
                    if (selfClose) tagName = tagName.TrimEnd('/').Trim();
                    
                    // Parse attributes
                    var attrs = new Dictionary<string, string>();
                    if (spaceIdx > 0)
                    {
                        var attrStr = selfClose ? tagContent[(spaceIdx + 1)..^1].Trim() : tagContent[(spaceIdx + 1)..];
                        var matches = System.Text.RegularExpressions.Regex.Matches(attrStr, "(\w+)=\"([^\"]*)\"");
                        foreach (System.Text.RegularExpressions.Match m in matches)
                            attrs[m.Groups[1].Value] = m.Groups[2].Value;
                    }
                    
                    i = end + 1;
                    
                    // Check for text content
                    if (!selfClose && i < xml.Length && xml[i] != '<')
                    {
                        var closeTag = $"</{tagName}>";
                        var closeIdx = xml.IndexOf(closeTag, i);
                        var value = xml[i..closeIdx].Trim();
                        if (tagStack.Count > 0 && !attrStack.Peek()) sb.Append(',');
                        sb.Append($"\"{tagName}\":"");
                        if (attrs.Count > 0)
                        {
                            sb.Append('{');
                            bool first = true;
                            if (!string.IsNullOrEmpty(value))
                            {
                                sb.Append($"\"_content\":\"{EscapeJson(value)}\"");
                                first = false;
                            }
                            foreach (var a in attrs)
                            {
                                if (!first) sb.Append(',');
                                sb.Append($"\"{a.Key}\":\"{EscapeJson(a.Value)}\"");
                                first = false;
                            }
                            sb.Append('}');
                        }
                        else
                        {
                            sb.Append($"\"{EscapeJson(value)}\"");
                        }
                        i = closeIdx + tagName.Length + 3;
                        if (i < xml.Length && tagStack.Count > 0) attrStack.Pop();
                        attrStack.Push(false);
                    }
                    else
                    {
                        if (tagStack.Count > 0) sb.Append(',');
                        sb.Append($"\"{tagName}\":");
                        
                        if (attrs.Count > 0 || selfClose)
                        {
                            sb.Append('{');
                            bool first = true;
                            foreach (var a in attrs)
                            {
                                if (!first) sb.Append(',');
                                sb.Append($"\"{a.Key}\":\"{EscapeJson(a.Value)}\"");
                                first = false;
                            }
                            sb.Append('}');
                            if (!selfClose) tagStack.Push(tagName);
                            if (!selfClose) attrStack.Push(true);
                        }
                        else
                        {
                            sb.Append('{');
                            if (!selfClose) tagStack.Push(tagName);
                            if (!selfClose) attrStack.Push(true);
                        }
                    }
                }
            }
            else i++;
        }
        while (tagStack.Count > 0) { sb.Append('}'); tagStack.Pop(); }
        sb.Append('}');
        return sb.ToString();
    }
    private static string EscapeJson(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
}
