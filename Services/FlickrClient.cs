using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using System.Xml.Linq;

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

    public (string Url, string RequestTokenSecret) GetAuthorizationUrl(string callbackUrl)
    {
        var oauthCallback = callbackUrl;
        var sig = Sign("GET", $"{BaseUrl}/oauth/request_token", new() { ["oauth_callback"] = oauthCallback }, null, null);
        var resp = _http.GetStringAsync($"{BaseUrl}/oauth/request_token?{sig}").Result;
        var parsed = HttpUtility.ParseQueryString(resp);
        var oauthToken = parsed["oauth_token"]!;
        var oauthTokenSecret = parsed["oauth_token_secret"]!;
        return ($"{BaseUrl}/oauth/authorize?oauth_token={oauthToken}&perms=delete", oauthTokenSecret);
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

    public async Task<string> UploadPhotoAsync(byte[] imageData, string filename, string title, string? description = null)
    {
        using var content = new MultipartFormDataContent();

        var imageContent = new ByteArrayContent(imageData);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(imageContent, "photo", filename);

        var oauthParams = new Dictionary<string, string>
        {
            ["oauth_token"] = _accessToken!,
            ["title"] = title,
            ["description"] = description ?? "",
            ["is_public"] = "0",
            ["is_friend"] = "0",
            ["is_family"] = "0"
        };

        var sig = Sign("POST", UploadUrl, oauthParams, _accessToken, _accessTokenSecret);
        var sigParams = HttpUtility.ParseQueryString(sig);
        foreach (var key in sigParams.AllKeys)
            oauthParams[key!] = sigParams[key!]!;

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

    public async Task<string?> FindPhotosetByTitleAsync(string title)
    {
        var sig = Sign("GET", $"{BaseUrl}/rest/", new()
        {
            ["method"] = "flickr.photosets.getList",
            ["api_key"] = _apiKey,
            ["oauth_token"] = _accessToken!
        }, _accessToken, _accessTokenSecret);
        var resp = await _http.GetStringAsync($"{BaseUrl}/rest/?{sig}");
        var doc = XDocument.Parse(resp);
        foreach (var ps in doc.Descendants("photoset"))
        {
            var titleEl = ps.Element("title");
            if (titleEl != null && titleEl.Value.Trim() == title)
                return ps.Attribute("id")?.Value;
        }
        return null;
    }

    public async Task<string> CreatePhotosetAsync(string title, string primaryPhotoId, string? description = null)
    {
        var sig = Sign("GET", $"{BaseUrl}/rest/", new()
        {
            ["method"] = "flickr.photosets.create",
            ["api_key"] = _apiKey,
            ["oauth_token"] = _accessToken!,
            ["title"] = title,
            ["primary_photo_id"] = primaryPhotoId,
            ["description"] = description ?? ""
        }, _accessToken, _accessTokenSecret);
        var resp = await _http.GetStringAsync($"{BaseUrl}/rest/?{sig}");
        var doc = XDocument.Parse(resp);
        var ps = doc.Descendants("photoset").FirstOrDefault();
        var id = ps?.Attribute("id")?.Value;
        if (id == null)
        {
            var err = doc.Descendants("err").FirstOrDefault();
            throw new Exception(err?.Attribute("msg")?.Value ?? "Unknown error creating photoset");
        }
        return id;
    }

    public async Task EditPhotosetMetaAsync(string photosetId, string title, string? description = null)
    {
        var sig = Sign("GET", $"{BaseUrl}/rest/", new()
        {
            ["method"] = "flickr.photosets.editMeta",
            ["api_key"] = _apiKey,
            ["oauth_token"] = _accessToken!,
            ["photoset_id"] = photosetId,
            ["title"] = title,
            ["description"] = description ?? ""
        }, _accessToken, _accessTokenSecret);
        var resp = await _http.GetStringAsync($"{BaseUrl}/rest/?{sig}");
        if (!resp.Contains("stat=\"ok\""))
        {
            var doc = XDocument.Parse(resp);
            var err = doc.Descendants("err").FirstOrDefault();
            throw new Exception(err?.Attribute("msg")?.Value ?? "Unknown error editing photoset");
        }
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
            var doc = XDocument.Parse(resp);
            foreach (var photo in doc.Descendants("photo"))
            {
                var id = photo.Attribute("id")?.Value;
                if (id != null) ids.Add(id);
            }
            var ps = doc.Descendants("photoset").FirstOrDefault();
            var pages = int.Parse(ps?.Attribute("pages")?.Value ?? "1");
            if (pages <= page) break;
            page++;
        }
        return ids;
    }

    public async Task<HashSet<string>> GetPhotosetPhotoTitlesAsync(string photosetId)
    {
        var titles = new HashSet<string>();
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
            var doc = XDocument.Parse(resp);
            foreach (var photo in doc.Descendants("photo"))
            {
                var title = photo.Attribute("title")?.Value;
                if (title != null) titles.Add(title);
            }
            var ps = doc.Descendants("photoset").FirstOrDefault();
            var pages = int.Parse(ps?.Attribute("pages")?.Value ?? "1");
            if (pages <= page) break;
            page++;
        }
        return titles;
    }

    public async Task DeletePhotosetAsync(string photosetId)
    {
        var sig = Sign("GET", $"{BaseUrl}/rest/", new()
        {
            ["method"] = "flickr.photosets.delete",
            ["api_key"] = _apiKey,
            ["oauth_token"] = _accessToken!,
            ["photoset_id"] = photosetId
        }, _accessToken, _accessTokenSecret);
        var resp = await _http.GetStringAsync($"{BaseUrl}/rest/?{sig}");
        if (!resp.Contains("stat=\"ok\""))
            throw new Exception("Failed to delete photoset");
    }

    public async Task DeletePhotoAsync(string photoId)
    {
        var sig = Sign("GET", $"{BaseUrl}/rest/", new()
        {
            ["method"] = "flickr.photos.delete",
            ["api_key"] = _apiKey,
            ["oauth_token"] = _accessToken!,
            ["photo_id"] = photoId
        }, _accessToken, _accessTokenSecret);
        var resp = await _http.GetStringAsync($"{BaseUrl}/rest/?{sig}");
        if (!resp.Contains("stat=\"ok\""))
            throw new Exception("Failed to delete photo");
    }

    public async Task<List<(string Id, string Title, string Description)>> GetPhotosetListPageAsync(int page, int perPage)
    {
        var results = new List<(string Id, string Title, string Description)>();
        var sig = Sign("GET", $"{BaseUrl}/rest/", new()
        {
            ["method"] = "flickr.photosets.getList",
            ["api_key"] = _apiKey,
            ["oauth_token"] = _accessToken!,
            ["page"] = page.ToString(),
            ["per_page"] = perPage.ToString()
        }, _accessToken, _accessTokenSecret);
        var resp = await _http.GetStringAsync($"{BaseUrl}/rest/?{sig}");
        var doc = XDocument.Parse(resp);
        foreach (var ps in doc.Descendants("photoset"))
        {
            var id = ps.Attribute("id")?.Value ?? "";
            var title = ps.Element("title")?.Value ?? "";
            var desc = ps.Element("description")?.Value ?? "";
            if (!string.IsNullOrEmpty(id))
                results.Add((id, title, desc));
        }
        return results;
    }

    public async Task<List<string>> GetPhotosetPhotoIdsByTitleAsync(string photosetId, string title)
    {
        var ids = new List<string>();
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
            var doc = XDocument.Parse(resp);
            foreach (var photo in doc.Descendants("photo"))
            {
                if (photo.Attribute("title")?.Value == title)
                {
                    var pid = photo.Attribute("id")?.Value;
                    if (pid != null) ids.Add(pid);
                }
            }
            var ps = doc.Descendants("photoset").FirstOrDefault();
            var pages = int.Parse(ps?.Attribute("pages")?.Value ?? "1");
            if (pages <= page) break;
            page++;
        }
        return ids;
    }

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
}