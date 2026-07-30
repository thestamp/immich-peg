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
        var sig = Sign("GET", $"{BaseUrl}/rest", new()
        {
            ["method"] = "flickr.photosets.getList",
            ["api_key"] = _apiKey,
            ["oauth_token"] = _accessToken!
        }, _accessToken, _accessTokenSecret);
        var url = $"{BaseUrl}/rest/?{sig}";
        Console.WriteLine($"[FlickrFindPhotoset] URL: {url}");
        var resp = await _http.GetStringAsync(url);
        var doc = JsonDocument.Parse(FlickrXmlToJson(resp));
        var photosets = doc.RootElement.GetProperty("photosets").GetProperty("photoset");
        foreach (var ps in photosets.EnumerateArray())
        {
            if (ps.GetProperty("title").GetProperty("_content").GetString() == title)
                return ps.GetProperty("id").GetString();
        }
        return null;
    }

    public async Task<string> CreatePhotosetAsync(string title, string primaryPhotoId)
    {
        var sig = Sign("GET", $"{BaseUrl}/rest", new()
        {
            ["method"] = "flickr.photosets.create",
            ["api_key"] = _apiKey,
            ["oauth_token"] = _accessToken!,
            ["title"] = title,
            ["primary_photo_id"] = primaryPhotoId
        }, _accessToken, _accessTokenSecret);
        var url = $"{BaseUrl}/rest/?{sig}";
        Console.WriteLine($"[FlickrCreatePhotoset] URL: {url}");
        var resp = await _http.GetStringAsync(url);
        var doc = JsonDocument.Parse(FlickrXmlToJson(resp));
        return doc.RootElement.GetProperty("photoset").GetProperty("id").GetString()!;
    }

    public async Task AddPhotoToPhotosetAsync(string photosetId, string photoId)
    {
        var sig = Sign("GET", $"{BaseUrl}/rest", new()
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
            var sig = Sign("GET", $"{BaseUrl}/rest", new()
            {
                ["method"] = "flickr.photosets.getPhotos",
                ["api_key"] = _apiKey,
                ["oauth_token"] = _accessToken!,
                ["photoset_id"] = photosetId,
                ["page"] = page.ToString(),
                ["per_page"] = "500"
            }, _accessToken, _accessTokenSecret);
            var resp = await _http.GetStringAsync($"{BaseUrl}/rest/?{sig}");
            var doc = JsonDocument.Parse(FlickrXmlToJson(resp));
            var ps = doc.RootElement.GetProperty("photoset");
            var photos = ps.GetProperty("photo");
            foreach (var p in photos.EnumerateArray())
                ids.Add(p.GetProperty("id").GetString()!);
            if (ps.GetProperty("pages").GetInt32() <= page) break;
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
            var sig = Sign("GET", $"{BaseUrl}/rest", new()
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
        Console.WriteLine($"[FlickrSign] apiSecret={_apiSecret} tokenSecret={tokenSecret} signingKey={signingKey}");
        Console.WriteLine($"[FlickrSign] baseString={baseString}");
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(signingKey));
        oauth["oauth_signature"] = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(baseString)));
        allParams["oauth_signature"] = oauth["oauth_signature"];

        return string.Join("&", allParams.OrderBy(k => k.Key)
            .Select(k => $"{Uri.EscapeDataString(k.Key)}={Uri.EscapeDataString(k.Value)}"));
    }

    // ── Simple XML→JSON converter for Flickr responses ────────────

    private static string FlickrXmlToJson(string xml)
    {
        // Minimal XML-to-JSON for Flickr's flat response format
        var sb = new StringBuilder();
        sb.Append('{');
        var tagStack = new Stack<string>();
        int i = 0;
        while (i < xml.Length)
        {
            if (xml[i] == '<' && (i + 1 < xml.Length) && xml[i + 1] != '/' && xml[i + 1] != '?')
            {
                var end = xml.IndexOf('>', i);
                var tag = xml[(i + 1)..end];
                var spaceIdx = tag.IndexOf(' ');
                if (spaceIdx > 0) tag = tag[..spaceIdx];
                i = end + 1;
                if (i < xml.Length && xml[i] != '<')
                {
                    var closeStart = xml.IndexOf($"</{tag}>", i);
                    var value = xml[i..closeStart].Trim();
                    if (tagStack.Count > 0) sb.Append(',');
                    sb.Append($"\"{tag}\":\"{EscapeJson(value)}\"");
                    i = closeStart + tag.Length + 3;
                }
                else
                {
                    if (tagStack.Count > 0) sb.Append(',');
                    sb.Append($"\"{tag}\":{{");
                    tagStack.Push(tag);
                }
            }
            else if (xml[i] == '<' && xml[i + 1] == '/')
            {
                var end = xml.IndexOf('>', i);
                var tag = xml[(i + 2)..end];
                i = end + 1;
                if (tagStack.Count > 0 && tagStack.Peek() == tag)
                {
                    tagStack.Pop();
                    sb.Append('}');
                }
            }
            else i++;
        }
        // Close any unclosed objects
        while (tagStack.Count > 0) { sb.Append('}'); tagStack.Pop(); }
        sb.Append('}');
        return sb.ToString();
    }

    private static string EscapeJson(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
}
