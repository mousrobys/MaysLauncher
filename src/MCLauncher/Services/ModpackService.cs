using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MCLauncher.Models;

namespace MCLauncher.Services;

/// <summary>
/// Установка модпаков формата Modrinth (.mrpack).
/// Внутри это ZIP с modrinth.index.json (список файлов и загрузчик)
/// и папкой overrides/ с конфигами, которые кладутся поверх сборки.
/// </summary>
public sealed class ModpackService
{
    private readonly HttpClient _http;

    public ModpackService(HttpClient http) => _http = http;

    public event Action<string>? Status;
    public event Action<DownloadProgress>? Progress;

    private void Report(string s) { Status?.Invoke(s); Log.Info(s); }

    /// <summary>Разобранные метаданные модпака.</summary>
    public sealed class PackInfo
    {
        public required string Name { get; init; }
        public string Version { get; init; } = "";
        public string Summary { get; init; } = "";
        public required string McVersion { get; init; }
        public required LoaderKind Loader { get; init; }
        public string? LoaderVersion { get; init; }
        public int FileCount { get; init; }
    }

    private sealed class PackFile
    {
        public required string Path { get; init; }
        public required string Url { get; init; }
        public string? Sha1 { get; init; }
        public long Size { get; init; }
        public bool Required { get; init; } = true;
    }

    // =====================================================================
    //  ЧТЕНИЕ .mrpack
    // =====================================================================

    public PackInfo ReadInfo(string packPath)
    {
        using var zip = ZipFile.OpenRead(packPath);

        // Формат определяем по содержимому, а не по расширению
        if (zip.GetEntry("manifest.json") is not null && zip.GetEntry("modrinth.index.json") is null)
            return ParseCurseManifest(zip).Info;

        return ParseIndex(zip).Info;
    }

    // =====================================================================
    //  CURSEFORGE (.zip с manifest.json)
    // =====================================================================

    private (PackInfo Info, List<CurseFile> Files) ParseCurseManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry("manifest.json")
                    ?? throw new InvalidDataException("В архиве нет manifest.json.");

        using var stream = entry.Open();
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "Модпак" : "Модпак";
        var version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";

        var mcVersion = "";
        var loader = LoaderKind.Vanilla;
        string? loaderVersion = null;

        if (root.TryGetProperty("minecraft", out var mc))
        {
            mcVersion = mc.TryGetProperty("version", out var mv) ? mv.GetString() ?? "" : "";

            if (mc.TryGetProperty("modLoaders", out var loaders) &&
                loaders.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in loaders.EnumerateArray())
                {
                    // id выглядит как "forge-47.2.0" или "fabric-0.15.7"
                    var id = l.TryGetProperty("id", out var lid) ? lid.GetString() ?? "" : "";
                    if (id.Length == 0) continue;

                    var dash = id.IndexOf('-');
                    if (dash < 0) continue;

                    var kind = id[..dash].ToLowerInvariant();
                    var ver = id[(dash + 1)..];

                    switch (kind)
                    {
                        case "forge":
                            loader = LoaderKind.Forge;
                            loaderVersion = $"{mcVersion}-{ver}";
                            break;
                        case "neoforge":
                            loader = LoaderKind.NeoForge;
                            loaderVersion = ver;
                            break;
                        case "fabric":
                            loader = LoaderKind.Fabric;
                            loaderVersion = ver;
                            break;
                    }

                    if (loader != LoaderKind.Vanilla) break;
                }
            }
        }

        if (string.IsNullOrEmpty(mcVersion))
            throw new InvalidDataException("В манифесте не указана версия Minecraft.");

        var files = new List<CurseFile>();

        if (root.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in filesEl.EnumerateArray())
            {
                if (!f.TryGetProperty("projectID", out var pid)) continue;
                if (!f.TryGetProperty("fileID", out var fid)) continue;

                files.Add(new CurseFile
                {
                    ProjectId = pid.GetInt64(),
                    FileId = fid.GetInt64(),
                    Required = !f.TryGetProperty("required", out var req) || req.GetBoolean()
                });
            }
        }

        var info = new PackInfo
        {
            Name = name,
            Version = version,
            Summary = "Модпак CurseForge",
            McVersion = mcVersion,
            Loader = loader,
            LoaderVersion = loaderVersion,
            FileCount = files.Count(x => x.Required)
        };

        return (info, files);
    }

    private sealed class CurseFile
    {
        public long ProjectId { get; init; }
        public long FileId { get; init; }
        public bool Required { get; init; } = true;
    }

    /// <summary>
    /// Ставит модпак CurseForge. Моды качаются с edge.forgecdn.net —
    /// ссылка собирается из fileID, поэтому API-ключ не нужен.
    /// </summary>
    private async Task<PackInfo> InstallCurseAsync(
        ZipArchive zip, GameInstance instance, CancellationToken ct)
    {
        var (info, files) = ParseCurseManifest(zip);
        var target = InstanceService.InstanceDir(instance);
        var modsDir = InstanceService.ModsDir(instance);
        Directory.CreateDirectory(modsDir);

        Report($"Модпак CurseForge «{info.Name}»: {info.FileCount} модов, " +
               $"{info.McVersion} {info.Loader.Display()}");

        var needed = files.Where(f => f.Required).ToList();
        var done = 0;
        var failed = 0;

        await Parallel.ForEachAsync(needed,
            new ParallelOptions { MaxDegreeOfParallelism = 5, CancellationToken = ct },
            async (file, token) =>
            {
                try
                {
                    var name = await DownloadCurseFileAsync(file.ProjectId, file.FileId, modsDir, token)
                        .ConfigureAwait(false);

                    var d = Interlocked.Increment(ref done);

                    Progress?.Invoke(new DownloadProgress
                    {
                        Stage = "Загрузка модпака",
                        CurrentFile = name,
                        FilesDone = d,
                        FilesTotal = needed.Count,
                        BytesDone = d,
                        BytesTotal = needed.Count
                    });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    Log.Warn($"CurseForge файл {file.ProjectId}/{file.FileId}: {ex.Message}");
                }
            }).ConfigureAwait(false);

        // overrides
        Report("Применяю конфигурацию модпака...");
        ExtractOverrides(zip, target, ct);

        if (failed > 0)
            Report($"Внимание: {failed} мод(ов) не скачалось. " +
                   "Некоторые авторы запрещают загрузку вне сайта CurseForge.");

        Report($"Модпак «{info.Name}» установлен: {done} из {needed.Count} модов.");
        return info;
    }

    /// <summary>Прямая ссылка на файл CurseForge собирается из его id.</summary>
    private async Task<string> DownloadCurseFileAsync(
        long projectId, long fileId, string modsDir, CancellationToken ct)
    {
        // Пробуем через API — так узнаём настоящее имя файла
        string? fileName = null;
        string? url = null;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.curseforge.com/v1/mods/{projectId}/files/{fileId}");
            req.Headers.TryAddWithoutValidation("x-api-key", ModService.CurseForgeApiKey);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(
                    await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    fileName = data.TryGetProperty("fileName", out var fn) ? fn.GetString() : null;
                    url = data.TryGetProperty("downloadUrl", out var du) ? du.GetString() : null;
                }
            }
        }
        catch { /* ключ может не давать доступа — соберём ссылку вручную */ }

        fileName ??= $"{projectId}-{fileId}.jar";

        // Схема CDN: /files/<первые цифры>/<последние 3>/<имя>
        url ??= $"https://edge.forgecdn.net/files/{fileId / 1000}/{fileId % 1000}/{Uri.EscapeDataString(fileName)}";

        var dst = Path.Combine(modsDir, fileName);
        if (File.Exists(dst)) return fileName;

        using var dlReq = new HttpRequestMessage(HttpMethod.Get, url);
        dlReq.Headers.TryAddWithoutValidation("User-Agent", "MaysLauncher/1.0");

        using var dl = await _http.SendAsync(dlReq, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        dl.EnsureSuccessStatusCode();

        var tmp = dst + ".part";
        await using (var src = await dl.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var outStream = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                         FileShare.None, 81920, true))
        {
            await src.CopyToAsync(outStream, ct).ConfigureAwait(false);
        }

        if (File.Exists(dst)) File.Delete(dst);
        File.Move(tmp, dst);

        return fileName;
    }

    private static void ExtractOverrides(ZipArchive zip, string target, CancellationToken ct)
    {
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var full = entry.FullName.Replace('\\', '/');

            string? rel = null;
            foreach (var prefix in new[] { "overrides/", "client-overrides/" })
            {
                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    rel = full[prefix.Length..];
                    break;
                }
            }

            if (rel is null || rel.Length == 0) continue;
            if (rel.Contains("..")) continue;
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var dst = Path.Combine(target, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

            try { entry.ExtractToFile(dst, true); }
            catch (Exception ex) { Log.Warn($"overrides {rel}: {ex.Message}"); }
        }
    }

    private static (PackInfo Info, List<PackFile> Files) ParseIndex(ZipArchive zip)
    {
        var entry = zip.GetEntry("modrinth.index.json")
                    ?? throw new InvalidDataException(
                        "В архиве нет modrinth.index.json — это не модпак Modrinth.");

        using var stream = entry.Open();
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "Модпак" : "Модпак";
        var version = root.TryGetProperty("versionId", out var v) ? v.GetString() ?? "" : "";
        var summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";

        var mcVersion = "";
        var loader = LoaderKind.Vanilla;
        string? loaderVersion = null;

        if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
        {
            foreach (var d in deps.EnumerateObject())
            {
                var val = d.Value.GetString() ?? "";
                switch (d.Name)
                {
                    case "minecraft": mcVersion = val; break;
                    case "fabric-loader": loader = LoaderKind.Fabric; loaderVersion = val; break;
                    case "forge": loader = LoaderKind.Forge; loaderVersion = val; break;
                    case "neoforge": loader = LoaderKind.NeoForge; loaderVersion = val; break;
                    case "quilt-loader":
                        throw new NotSupportedException(
                            "Модпак требует Quilt — этот загрузчик пока не поддерживается.");
                }
            }
        }

        if (string.IsNullOrEmpty(mcVersion))
            throw new InvalidDataException("В модпаке не указана версия Minecraft.");

        // Forge в mrpack указывается без префикса версии игры
        if (loader == LoaderKind.Forge && loaderVersion is not null && !loaderVersion.Contains('-'))
            loaderVersion = $"{mcVersion}-{loaderVersion}";

        var files = new List<PackFile>();

        if (root.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in filesEl.EnumerateArray())
            {
                var path = f.TryGetProperty("path", out var p) ? p.GetString() : null;
                if (string.IsNullOrEmpty(path)) continue;

                // Защита от выхода за пределы папки сборки
                if (path.Contains("..") || Path.IsPathRooted(path)) continue;

                string? url = null;
                if (f.TryGetProperty("downloads", out var dls) && dls.ValueKind == JsonValueKind.Array)
                    url = dls.EnumerateArray().FirstOrDefault().GetString();

                if (string.IsNullOrEmpty(url)) continue;

                string? sha1 = null;
                if (f.TryGetProperty("hashes", out var h) && h.TryGetProperty("sha1", out var s1))
                    sha1 = s1.GetString();

                var required = true;
                if (f.TryGetProperty("env", out var env) && env.TryGetProperty("client", out var cl))
                    required = cl.GetString() != "unsupported";

                files.Add(new PackFile
                {
                    Path = path!,
                    Url = url!,
                    Sha1 = sha1,
                    Size = f.TryGetProperty("fileSize", out var fs) ? fs.GetInt64() : 0,
                    Required = required
                });
            }
        }

        var info = new PackInfo
        {
            Name = name,
            Version = version,
            Summary = summary,
            McVersion = mcVersion,
            Loader = loader,
            LoaderVersion = loaderVersion,
            FileCount = files.Count(x => x.Required)
        };

        return (info, files);
    }

    // =====================================================================
    //  УСТАНОВКА
    // =====================================================================

    /// <summary>Распаковывает модпак в папку сборки: качает моды и копирует overrides.</summary>
    public async Task<PackInfo> InstallAsync(
        string mrpackPath, GameInstance instance, CancellationToken ct = default)
    {
        InstanceService.EnsureFolders(instance);
        var target = InstanceService.InstanceDir(instance);

        using var zip = ZipFile.OpenRead(mrpackPath);

        // CurseForge-архивы обрабатываем отдельной веткой
        if (zip.GetEntry("manifest.json") is not null && zip.GetEntry("modrinth.index.json") is null)
            return await InstallCurseAsync(zip, instance, ct).ConfigureAwait(false);

        var (info, files) = ParseIndex(zip);

        Report($"Модпак «{info.Name}»: {info.FileCount} файлов, {info.McVersion} {info.Loader.Display()}");

        // 1. Скачиваем моды из индекса
        var needed = files.Where(f => f.Required).ToList();
        long totalBytes = needed.Sum(f => f.Size > 0 ? f.Size : 262144);
        long doneBytes = 0;
        var doneFiles = 0;

        await Parallel.ForEachAsync(needed,
            new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct },
            async (file, token) =>
            {
                var dst = Path.Combine(target, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

                if (File.Exists(dst) && await IsValidAsync(dst, file.Sha1, file.Size, token).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref doneFiles);
                    return;
                }

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, file.Url);
                    req.Headers.TryAddWithoutValidation("User-Agent", "MaysLauncher/1.0");

                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token)
                        .ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();

                    var tmp = dst + ".part";
                    await using (var src = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                    await using (var outStream = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                                     FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = await src.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                        {
                            await outStream.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                            var d = Interlocked.Add(ref doneBytes, read);

                            Progress?.Invoke(new DownloadProgress
                            {
                                Stage = "Загрузка модпака",
                                CurrentFile = Path.GetFileName(file.Path),
                                BytesDone = d,
                                BytesTotal = totalBytes,
                                FilesDone = doneFiles,
                                FilesTotal = needed.Count
                            });
                        }
                    }

                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(tmp, dst);

                    Interlocked.Increment(ref doneFiles);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Warn($"Модпак: не удалось скачать {file.Path}: {ex.Message}");
                }
            }).ConfigureAwait(false);

        // 2. Копируем overrides поверх
        Report("Применяю конфигурацию модпака...");

        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var full = entry.FullName.Replace('\\', '/');

            string? rel = null;
            if (full.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase))
                rel = full["overrides/".Length..];
            else if (full.StartsWith("client-overrides/", StringComparison.OrdinalIgnoreCase))
                rel = full["client-overrides/".Length..];

            if (rel is null || rel.Length == 0) continue;
            if (rel.Contains("..")) continue;
            if (string.IsNullOrEmpty(entry.Name)) continue;   // директория

            var dst = Path.Combine(target, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

            try { entry.ExtractToFile(dst, true); }
            catch (Exception ex) { Log.Warn($"overrides {rel}: {ex.Message}"); }
        }

        Report($"Модпак «{info.Name}» установлен: {doneFiles} из {needed.Count} файлов.");
        return info;
    }

    /// <summary>Скачивает .mrpack по прямой ссылке во временный файл.</summary>
    public async Task<string> DownloadPackAsync(string url, CancellationToken ct = default)
    {
        Directory.CreateDirectory(LauncherPaths.CacheDir);

        var name = Path.GetFileName(new Uri(url).AbsolutePath);
        if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
            name = "modpack.mrpack";

        var path = Path.Combine(LauncherPaths.CacheDir, name);

        Report($"Скачиваю {name}...");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "MaysLauncher/1.0");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? 0;

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long done = 0;
        int read;

        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;

            Progress?.Invoke(new DownloadProgress
            {
                Stage = "Загрузка модпака", CurrentFile = name,
                BytesDone = done, BytesTotal = total
            });
        }

        return path;
    }

    private static async Task<bool> IsValidAsync(string path, string? sha1, long size, CancellationToken ct)
    {
        try
        {
            var fi = new FileInfo(path);
            if (size > 0 && fi.Length != size) return false;
            if (string.IsNullOrEmpty(sha1)) return true;

            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            using var sha = SHA1.Create();
            var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);

            return string.Equals(Convert.ToHexString(hash), sha1, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
