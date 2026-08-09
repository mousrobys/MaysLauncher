using System.Text.Json;
using MCLauncher.Models;

namespace MCLauncher.Services;

public class NewsItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string Date { get; set; } = "";
    public bool Important { get; set; } = false;
}

public class SponsorServerEntry
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string Description { get; set; } = "";
    public string Site { get; set; } = "";
    public string RequiredVersion { get; set; } = "";
    public bool Featured { get; set; } = true;
}

public class LauncherConfig
{
    public List<NewsItem> News { get; set; } = new();
    public List<SponsorServerEntry> SponsorServers { get; set; } = new();
}

public class NewsService
{
    private readonly HttpClient _http;
    private const string ConfigUrl = "https://raw.githubusercontent.com/mousrobys/MaysLauncher/master/launcher-config.json";
    private const string FallbackConfigUrl = "https://api.github.com/repos/mousrobys/MaysLauncher/contents/launcher-config.json";

    public NewsService(HttpClient http)
    {
        _http = http;
    }

    public async Task<LauncherConfig> GetConfigAsync()
    {
        try
        {
            var response = await _http.GetStringAsync(ConfigUrl);
            var config = JsonSerializer.Deserialize<LauncherConfig>(response, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return config ?? new LauncherConfig();
        }
        catch
        {
            try
            {
                var response = await _http.GetStringAsync(FallbackConfigUrl);
                var doc = JsonDocument.Parse(response);
                var base64 = doc.RootElement.GetProperty("content").GetString();
                if (base64 != null)
                {
                    var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64.Replace("\n", "")));
                    var config = JsonSerializer.Deserialize<LauncherConfig>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    return config ?? new LauncherConfig();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось загрузить конфиг новостей: " + ex.Message);
            }
        }
        return new LauncherConfig();
    }
}
