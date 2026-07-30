using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ImmichPeg.Services;

public class ImmichClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public static readonly string[] MainRequiredPermissions = ["asset.read", "album.read"];
    public static readonly string[] PublicRequiredPermissions = [
        "asset.upload", "asset.create", "album.read", "album.create",
        "album.update", "album.delete", "shared-link.read", "shared-link.create", "shared-link.delete"
    ];

    public ImmichClient(string baseUrl, string apiKey)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient
        {
            BaseAddress = new Uri($"{_baseUrl}/api/"),
            Timeout = TimeSpan.FromSeconds(60)
        };
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ImmichPeg", "2.0"));
    }

    public void Dispose() => _http.Dispose();

    public async Task<bool> PingAsync()
    {
        try
        {
            var resp = await _http.GetAsync("server/version");
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<List<JsonElement>> GetAllAlbumsAsync(bool? shared = null)
    {
        var albums = new List<JsonElement>();
        var page = 1;
        while (true)
        {
            var query = $"albums?page={page}&size=500";
            if (shared.HasValue)
            {
                query += $"&shared={shared.Value.ToString().ToLower()}";
            }
            var resp = await _http.GetAsync(query);
            resp.EnsureSuccessStatusCode();
            var data = await JsonSerializer.DeserializeAsync<JsonElement>(await resp.Content.ReadAsStreamAsync());
            if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0) break;
            foreach (var item in data.EnumerateArray())
                albums.Add(item.Clone());
            if (data.GetArrayLength() < 500) break;
            page++;
        }
        return albums;
    }

    public async Task<JsonElement> GetAlbumAsync(string albumId)
    {
        var resp = await _http.GetAsync($"albums/{albumId}");
        resp.EnsureSuccessStatusCode();
        return await JsonSerializer.DeserializeAsync<JsonElement>(await resp.Content.ReadAsStreamAsync());
    }

    public async Task<JsonElement> CreateAlbumAsync(string name, string description = "")
    {
        var payload = JsonSerializer.Serialize(new { albumName = name, description });
        var resp = await _http.PostAsync("albums", new StringContent(payload, Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();
        return await JsonSerializer.DeserializeAsync<JsonElement>(await resp.Content.ReadAsStreamAsync());
    }

    public async Task DeleteAlbumAsync(string albumId)
    {
        var resp = await _http.DeleteAsync($"albums/{albumId}");
        resp.EnsureSuccessStatusCode();
    }

    public async Task AddAssetsToAlbumAsync(string albumId, List<string> assetIds)
    {
        var payload = JsonSerializer.Serialize(new { ids = assetIds });
        var resp = await _http.PutAsync($"albums/{albumId}/assets", new StringContent(payload, Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();
    }

    public async Task<List<JsonElement>> GetAlbumAssetsAsync(string albumId)
    {
        var assets = new List<JsonElement>();
        var page = 1;
        while (true)
        {
            var payload = JsonSerializer.Serialize(new { albumIds = new[] { albumId }, page, size = 1000 });
            var resp = await _http.PostAsync("search/metadata", new StringContent(payload, Encoding.UTF8, "application/json"));
            resp.EnsureSuccessStatusCode();
            var data = await JsonSerializer.DeserializeAsync<JsonElement>(await resp.Content.ReadAsStreamAsync());
            var items = data.GetProperty("assets").GetProperty("items");
            if (items.GetArrayLength() == 0) break;
            foreach (var item in items.EnumerateArray())
                assets.Add(item.Clone());
            if (items.GetArrayLength() < 1000) break;
            page++;
        }
        return assets;
    }

    public async Task<Dictionary<string, string>> GetAllAssetFilenamesAsync()
    {
        var map = new Dictionary<string, string>();
        var page = 1;
        while (true)
        {
            var payload = JsonSerializer.Serialize(new { page, size = 1000 });
            var resp = await _http.PostAsync("search/metadata", new StringContent(payload, Encoding.UTF8, "application/json"));
            resp.EnsureSuccessStatusCode();
            var data = await JsonSerializer.DeserializeAsync<JsonElement>(await resp.Content.ReadAsStreamAsync());
            var items = data.GetProperty("assets").GetProperty("items");
            if (items.GetArrayLength() == 0) break;
            foreach (var item in items.EnumerateArray())
            {
                var name = item.TryGetProperty("originalFileName", out var n) ? n.GetString() ?? "" : "";
                var id = item.GetProperty("id").GetString()!;
                if (!string.IsNullOrEmpty(name)) map[name] = id;
            }
            if (items.GetArrayLength() < 1000) break;
            page++;
        }
        return map;
    }

    public async Task<JsonElement> UploadAssetAsync(byte[] assetData, string filename, string fileCreatedAt, string fileModifiedAt)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(assetData), "assetData", filename);
        content.Add(new StringContent(fileCreatedAt), "fileCreatedAt");
        content.Add(new StringContent(fileModifiedAt), "fileModifiedAt");
        content.Add(new StringContent("false"), "isFavorite");

        var resp = await _http.PostAsync("assets", content);
        resp.EnsureSuccessStatusCode();
        return await JsonSerializer.DeserializeAsync<JsonElement>(await resp.Content.ReadAsStreamAsync());
    }

    public async Task<byte[]> DownloadAssetAsync(string assetId)
    {
        var resp = await _http.GetAsync($"assets/{assetId}/original");
        resp.EnsureSuccessStatusCode();
        using var ms = new MemoryStream();
        await resp.Content.CopyToAsync(ms);
        return ms.ToArray();
    }

    public async Task<List<JsonElement>> GetAlbumSharesAsync()
    {
        var resp = await _http.GetAsync("shared-links");
        resp.EnsureSuccessStatusCode();
        return (await JsonSerializer.DeserializeAsync<JsonElement>(await resp.Content.ReadAsStreamAsync()))
            .EnumerateArray().Select(x => x.Clone()).ToList();
    }

    public async Task<JsonElement?> GetShareForAlbumAsync(string albumId)
    {
        var shares = await GetAlbumSharesAsync();
        return shares.FirstOrDefault(s =>
            s.TryGetProperty("album", out var a) &&
            a.TryGetProperty("id", out var id) &&
            id.GetString() == albumId);
    }

    public async Task<JsonElement> CreateShareAsync(string albumId, string? slug, string description, bool allowDownload, bool showMetadata)
    {
        var payload = new Dictionary<string, object>
        {
            ["albumId"] = albumId,
            ["type"] = "ALBUM",
            ["description"] = description,
            ["allowDownload"] = allowDownload,
            ["showMetadata"] = showMetadata
        };
        if (!string.IsNullOrEmpty(slug)) payload["slug"] = slug!;

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync("shared-links", content);
        resp.EnsureSuccessStatusCode();
        return await JsonSerializer.DeserializeAsync<JsonElement>(await resp.Content.ReadAsStreamAsync());
    }

    public async Task DeleteShareAsync(string shareId)
    {
        var resp = await _http.DeleteAsync($"shared-links/{shareId}");
        resp.EnsureSuccessStatusCode();
    }

    public async Task<JsonElement> GetApiKeyInfoAsync()
    {
        var resp = await _http.GetAsync("api-keys/me");
        resp.EnsureSuccessStatusCode();
        return await JsonSerializer.DeserializeAsync<JsonElement>(await resp.Content.ReadAsStreamAsync());
    }
}
