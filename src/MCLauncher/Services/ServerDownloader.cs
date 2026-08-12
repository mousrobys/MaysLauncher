using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MCLauncher.Services;

public class ServerDownloadResult
{
    public bool Success;
    public string? Error;
    public string? FilePath;
}

public class ServerDownloader
{
    private readonly HttpClient _http;

    private static class Urls
    {
        public const string Purpur = "https://api.purpurmc.org/v2/purpur";
        public const string Paper = "https://api.papermc.io/v2/projects/paper/versions";
        public const string BuildTools = "https://hub.spigotmc.org/jenkins/job/BuildTools/lastSuccessfulBuild/artifact/target/BuildTools.jar";
        public const string VanillaManifest = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";
        public const string Fabric = "https://meta.fabricmc.net/v2/versions";
        public const string Forge = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
        public static string? PurpurMirror = null;
        public static string? PaperMirror = null;
        public static string? VanillaMirror = null;
    }

    public event Action<string>? OnStatus;
    public event Action<int>? OnProgress;

    public ServerDownloader(HttpClient http) { _http = http; }

    public async Task<ServerDownloadResult> DownloadAsync(ServerCoreType core, string mcVersion, string outputPath, CancellationToken ct = default)
    {
        try
        {
            OnStatus?.Invoke($"Загрузка {core.Display()} {mcVersion}...");
            return core switch
            {
                ServerCoreType.Purpur => await DownloadPurpurAsync(mcVersion, outputPath, ct),
                ServerCoreType.Paper => await DownloadPaperAsync(mcVersion, outputPath, ct),
                ServerCoreType.Bukkit => await DownloadBuildToolsAsync(outputPath, ct),
                ServerCoreType.Vanilla => await DownloadVanillaAsync(mcVersion, outputPath, ct),
                ServerCoreType.Fabric => await DownloadFabricAsync(mcVersion, outputPath, ct),
                ServerCoreType.Forge => await DownloadForgeAsync(mcVersion, outputPath, ct),
                _ => new ServerDownloadResult { Success = false, Error = $"Неизвестный тип ядра: {core}" }
            };
        }
        catch (Exception ex)
        {
            var msg = $"Ошибка загрузки {core.Display()}: {ex.Message}";
            OnStatus?.Invoke(msg);
            return new ServerDownloadResult { Success = false, Error = msg };
        }
    }

    private async Task<ServerDownloadResult> DownloadPurpurAsync(string mcVersion, string output, CancellationToken ct)
    {
        var baseUrl = Urls.PurpurMirror ?? Urls.Purpur;
        OnStatus?.Invoke("Получение информации о сборке Purpur...");
        try
        {
            var resp = await _http.GetStringAsync($"{baseUrl}/{mcVersion}", ct);
            using var doc = JsonDocument.Parse(resp);
            var latest = doc.RootElement.GetProperty("builds").GetProperty("latest").GetString();
            var url = $"{baseUrl}/{mcVersion}/{latest}/download";
            OnStatus?.Invoke("Скачивание Purpur...");
            return await DownloadFileAsync(url, output, ct);
        }
        catch (Exception ex) { return new ServerDownloadResult { Success = false, Error = $"Purpur: {ex.Message}" }; }
    }

    private async Task<ServerDownloadResult> DownloadPaperAsync(string mcVersion, string output, CancellationToken ct)
    {
        var baseUrl = Urls.PaperMirror ?? Urls.Paper;
        OnStatus?.Invoke("Получение информации о сборке Paper...");
        try
        {
            var resp = await _http.GetStringAsync($"{baseUrl}/{mcVersion}", ct);
            using var doc = JsonDocument.Parse(resp);
            var builds = doc.RootElement.GetProperty("builds");
            var latestBuild = builds[builds.GetArrayLength() - 1].GetInt32();
            var fileName = $"paper-{mcVersion}-{latestBuild}.jar";
            var url = $"{baseUrl}/{mcVersion}/builds/{latestBuild}/downloads/{fileName}";
            OnStatus?.Invoke("Скачивание Paper...");
            return await DownloadFileAsync(url, output, ct);
        }
        catch (Exception ex) { return new ServerDownloadResult { Success = false, Error = $"Paper: {ex.Message}" }; }
    }

    private async Task<ServerDownloadResult> DownloadBuildToolsAsync(string output, CancellationToken ct)
    {
        OnStatus?.Invoke("Скачивание BuildTools...");
        return await DownloadFileAsync(Urls.BuildTools, output, ct);
    }

    private async Task<ServerDownloadResult> DownloadVanillaAsync(string mcVersion, string output, CancellationToken ct)
    {
        OnStatus?.Invoke("Получение манифеста Vanilla...");
        try
        {
            var manifestUrl = Urls.VanillaMirror ?? Urls.VanillaManifest;
            var resp = await _http.GetStringAsync(manifestUrl, ct);
            using var doc = JsonDocument.Parse(resp);
            string? jarUrl = null;
            foreach (var v in doc.RootElement.GetProperty("versions").EnumerateArray())
            {
                if (v.GetProperty("id").GetString() == mcVersion) { jarUrl = v.GetProperty("url").GetString(); break; }
            }
            if (jarUrl == null) return new ServerDownloadResult { Success = false, Error = $"Версия {mcVersion} не найдена" };
            OnStatus?.Invoke("Получение ссылки на сервер...");
            var versionResp = await _http.GetStringAsync(jarUrl, ct);
            using var versionDoc = JsonDocument.Parse(versionResp);
            var serverUrl = versionDoc.RootElement.GetProperty("downloads").GetProperty("server").GetProperty("url").GetString();
            OnStatus?.Invoke("Скачивание Vanilla сервера...");
            return await DownloadFileAsync(serverUrl!, output, ct);
        }
        catch (Exception ex) { return new ServerDownloadResult { Success = false, Error = $"Vanilla: {ex.Message}" }; }
    }

    private async Task<ServerDownloadResult> DownloadFabricAsync(string mcVersion, string output, CancellationToken ct)
    {
        OnStatus?.Invoke("Получение информации о Fabric...");
        try
        {
            var resp = await _http.GetStringAsync($"{Urls.Fabric}/loader/{mcVersion}", ct);
            using var doc = JsonDocument.Parse(resp);
            if (doc.RootElement.GetArrayLength() == 0) return new ServerDownloadResult { Success = false, Error = $"Fabric для {mcVersion} не найден" };
            var loader = doc.RootElement[0].GetProperty("loader");
            var version = loader.GetProperty("version").GetString();
            var url = $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}/{version}/server/jar";
            OnStatus?.Invoke("Скачивание Fabric сервера...");
            return await DownloadFileAsync(url, output, ct);
        }
        catch (Exception ex) { return new ServerDownloadResult { Success = false, Error = $"Fabric: {ex.Message}" }; }
    }

    private async Task<ServerDownloadResult> DownloadForgeAsync(string mcVersion, string output, CancellationToken ct)
    {
        OnStatus?.Invoke("Получение информации о Forge...");
        try
        {
            var resp = await _http.GetStringAsync(Urls.Forge, ct);
            using var doc = JsonDocument.Parse(resp);
            var promo = $"{mcVersion}-latest";
            if (!doc.RootElement.GetProperty("promos").TryGetProperty(promo, out var forgeVersion))
                return new ServerDownloadResult { Success = false, Error = $"Forge для {mcVersion} не найден" };
            var version = forgeVersion.GetString();
            var fileName = $"forge-{mcVersion}-{version}-installer.jar";
            var url = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{mcVersion}-{version}/{fileName}";
            OnStatus?.Invoke("Скачивание Forge installer...");
            return await DownloadFileAsync(url, output, ct);
        }
        catch (Exception ex) { return new ServerDownloadResult { Success = false, Error = $"Forge: {ex.Message}" }; }
    }

    private async Task<ServerDownloadResult> DownloadFileAsync(string url, string output, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var totalBytes = resp.Content.Headers.ContentLength ?? -1L;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                totalRead += read;
                if (totalBytes > 0) OnProgress?.Invoke((int)(totalRead * 100 / totalBytes));
            }
            OnProgress?.Invoke(100);
            return new ServerDownloadResult { Success = true, FilePath = output };
        }
        catch (Exception ex) { return new ServerDownloadResult { Success = false, Error = ex.Message }; }
    }
}
