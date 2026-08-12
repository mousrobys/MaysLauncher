using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MCLauncher.Services;

public class PluginInfo
{
    public string Id = "";
    public string Title = "";
    public string Description = "";
    public string IconUrl = "";
    public long Downloads;
    public string? DownloadUrl;
    public string? FileName;
}

public class ServerBuildInfo
{
    public string Id = "";
    public string Title = "";
    public string Description = "";
    public string Type = "";
    public string DownloadUrl = "";
    public string McVersion = "1.20.4";
}

public class PluginManager
{
    private readonly HttpClient _http;
    private const string ApiBase = "https://api.modrinth.com/v2";

    public event Action<string>? OnStatus;
    public event Action<int>? OnProgress;

    public PluginManager(HttpClient http) { _http = http; }

    public List<ServerBuildInfo> GetPredefinedBuilds() => new()
    {
        new ServerBuildInfo { Id = "skyblock", Title = "SkyBlock", Description = "Классический SkyBlock с островами и экономикой", Type = "Выживание", McVersion = "1.20.4" },
        new ServerBuildInfo { Id = "anarchy", Title = "Анархия", Description = "Выживание без правил, гриферство разрешено", Type = "Анархия", McVersion = "1.20.4" },
        new ServerBuildInfo { Id = "survival", Title = "Выживание+", Description = "Vanilla+ с плагинами на экономику и защиту", Type = "Выживание", McVersion = "1.20.4" },
        new ServerBuildInfo { Id = "creative", Title = "Творческий", Description = "Мир для строительства с WorldEdit", Type = "Творческий", McVersion = "1.20.4" },
        new ServerBuildInfo { Id = "minigames", Title = "Мини-игры", Description = "Набор мини-игр: BedWars, SkyWars, Murder Mystery", Type = "Мини-игры", McVersion = "1.20.4" },
        new ServerBuildInfo { Id = "rpg", Title = "RPG World", Description = "Ролевой сервер с квестами, классами и данженами", Type = "RPG", McVersion = "1.20.4" },
    };

    public async Task<List<PluginInfo>> SearchPluginsAsync(string query, string mcVersion, CancellationToken ct = default)
    {
        try
        {
            OnStatus?.Invoke("Поиск плагинов...");

            var facets = JsonSerializer.Serialize(new[]
            {
                new[] { "categories:spigot", "categories:paper" },
                new[] { "project_type:plugin" },
                new[] { $@"versions:{mcVersion}" }
            });

            var url = $"{ApiBase}/search?query={Uri.EscapeDataString(query)}&facets={facets}&limit=20";
            var resp = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(resp);

            var results = new List<PluginInfo>();
            foreach (var hit in doc.RootElement.GetProperty("hits").EnumerateArray())
            {
                results.Add(new PluginInfo
                {
                    Id = hit.GetProperty("project_id").GetString() ?? "",
                    Title = hit.GetProperty("title").GetString() ?? "",
                    Description = hit.GetProperty("description").GetString() ?? "",
                    IconUrl = hit.TryGetProperty("icon_url", out var icon) ? icon.GetString() ?? "" : "",
                    Downloads = hit.GetProperty("downloads").GetInt64()
                });
            }

            OnStatus?.Invoke($"Найдено {results.Count} плагинов");
            return results;
        }
        catch (Exception ex)
        {
            OnStatus?.Invoke($"Ошибка поиска: {ex.Message}");
            return new List<PluginInfo>();
        }
    }

    public async Task<bool> GetDownloadInfoAsync(PluginInfo plugin, string mcVersion, CancellationToken ct = default)
    {
        try
        {
            var url = $"{ApiBase}/project/{plugin.Id}/version?game_versions=[{JsonSerializer.Serialize(mcVersion)}]";
            var resp = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(resp);

            if (doc.RootElement.GetArrayLength() == 0) return false;

            var version = doc.RootElement[0];
            var files = version.GetProperty("files");
            foreach (var file in files.EnumerateArray())
            {
                if (file.GetProperty("primary").GetBoolean())
                {
                    plugin.DownloadUrl = file.GetProperty("url").GetString();
                    plugin.FileName = file.GetProperty("filename").GetString();
                    return true;
                }
            }
            return false;
        }
        catch { return false; }
    }

    public async Task<bool> InstallPluginAsync(PluginInfo plugin, CancellationToken ct = default)
    {
        try
        {
            if (plugin.DownloadUrl == null || plugin.FileName == null)
                return false;

            var pluginsDir = ServerConfig.GetPluginsDir();
            Directory.CreateDirectory(pluginsDir);
            var output = Path.Combine(pluginsDir, plugin.FileName);

            OnStatus?.Invoke($"Скачивание {plugin.Title}...");

            using var resp = await _http.GetAsync(plugin.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long totalRead = 0;
            var totalBytes = resp.Content.Headers.ContentLength ?? -1L;
            int read;

            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                totalRead += read;
                if (totalBytes > 0) OnProgress?.Invoke((int)(totalRead * 100 / totalBytes));
            }

            OnProgress?.Invoke(100);
            OnStatus?.Invoke($"Плагин {plugin.Title} установлен!");
            return true;
        }
        catch (Exception ex)
        {
            OnStatus?.Invoke($"Ошибка установки: {ex.Message}");
            return false;
        }
    }
}
