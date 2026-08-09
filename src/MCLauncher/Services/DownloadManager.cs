using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using MCLauncher.Models;

namespace MCLauncher.Services;

public sealed class DownloadProgress
{
    public string Stage { get; init; } = "";
    public string CurrentFile { get; init; } = "";
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
    public int FilesDone { get; init; }
    public int FilesTotal { get; init; }

    public double Percent => BytesTotal > 0
        ? Math.Clamp(BytesDone * 100.0 / BytesTotal, 0, 100)
        : (FilesTotal > 0 ? Math.Clamp(FilesDone * 100.0 / FilesTotal, 0, 100) : 0);
}

public sealed class DownloadTask
{
    public required string Url { get; init; }
    public required string TargetPath { get; init; }
    public string? Sha1 { get; init; }
    public long Size { get; init; }
    public string Display { get; init; } = "";
}

/// <summary>
/// Скачивание клиента, библиотек, нативных и ассетов Minecraft
/// с проверкой SHA1, докачкой и параллелизмом.
/// </summary>
public sealed class DownloadManager
{
    private const string ResourcesBase = "https://resources.download.minecraft.net";
    private const string LibrariesMirror = "https://libraries.minecraft.net/";

    private readonly HttpClient _http;
    private readonly int _parallelism;

    public DownloadManager(HttpClient http, int parallelism = 8)
    {
        _http = http;
        _parallelism = Math.Max(1, parallelism);
    }

    /// <summary>Куда складывать файлы. По умолчанию — общее хранилище лаунчера.</summary>
    public GamePaths Paths { get; set; } = GamePaths.Shared;

    public event Action<DownloadProgress>? Progress;

    // =====================================================================
    //  ПОЛНАЯ УСТАНОВКА ВЕРСИИ
    // =====================================================================

    public sealed class InstallResult
    {
        public required VersionDetail Detail { get; init; }
        public required List<string> ClasspathJars { get; init; }
        public required string ClientJar { get; init; }
        /// <summary>Значение для ${natives_directory} (корень).</summary>
        public required string NativesDir { get; init; }
        /// <summary>Куда реально распакованы .dll (для 1.21.2+ это подпапка, напр. natives\&lt;ver&gt;\java).</summary>
        public required string NativesExtractDir { get; init; }
        public required string AssetsDir { get; init; }
        public required string AssetIndexId { get; init; }
        public bool AssetsAreVirtual { get; init; }
    }

    /// <summary>
    /// Определяет папку, из которой JVM будет грузить нативные библиотеки.
    /// Начиная с 1.21.2 Mojang указывает в JSON "-Djava.library.path=${natives_directory}/java",
    /// то есть DLL должны лежать в подпапке, а не в корне natives.
    /// </summary>
    public static string ResolveNativesExtractDir(VersionDetail detail, string nativesRoot)
    {
        if (detail.Arguments is not { } root ||
            root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("jvm", out var jvm) ||
            jvm.ValueKind != JsonValueKind.Array)
            return nativesRoot;

        const string prefix = "-Djava.library.path=";

        foreach (var raw in EnumerateArgumentStrings(jvm))
        {
            if (!raw.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var value = raw[prefix.Length..]
                .Replace("${natives_directory}", nativesRoot)
                .Replace('/', Path.DirectorySeparatorChar)
                .Trim('"');

            return string.IsNullOrWhiteSpace(value) ? nativesRoot : Path.GetFullPath(value);
        }

        return nativesRoot;
    }

    /// <summary>Плоский перебор строковых значений массива аргументов (с учётом правил ОС).</summary>
    private static IEnumerable<string> EnumerateArgumentStrings(JsonElement array)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s)) yield return s!;
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object) continue;

            List<Rule>? rules = null;
            if (item.TryGetProperty("rules", out var rulesEl))
            {
                try { rules = JsonSerializer.Deserialize<List<Rule>>(rulesEl.GetRawText()); }
                catch { rules = null; }
            }

            if (!RuleEvaluator.Allows(rules)) continue;
            if (!item.TryGetProperty("value", out var v)) continue;

            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrEmpty(s)) yield return s!;
            }
            else if (v.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in v.EnumerateArray())
                {
                    var s = e.GetString();
                    if (!string.IsNullOrEmpty(s)) yield return s!;
                }
            }
        }
    }

    public async Task<InstallResult> InstallVersionAsync(
        VersionDetail detail, CancellationToken ct = default)
    {
        Paths.EnsureAll();

        var versionDir = Paths.VersionDir(detail.Id);
        Directory.CreateDirectory(versionDir);

        var nativesDir = Paths.NativesDir(detail.Id);
        Directory.CreateDirectory(nativesDir);

        // 1.21.2+ ожидает DLL в подпапке (${natives_directory}/java), более старые — в корне.
        var nativesExtractDir = ResolveNativesExtractDir(detail, nativesDir);
        Directory.CreateDirectory(nativesExtractDir);

        // --- 1. Собираем список загрузок -------------------------------
        var tasks = new List<DownloadTask>();
        var classpath = new List<string>();
        var nativeArchives = new List<(string archive, ExtractRule? extract)>();

        // client.jar
        var clientJar = Paths.VersionJar(detail.Id);
        if (detail.Downloads is not null && detail.Downloads.TryGetValue("client", out var client))
        {
            tasks.Add(new DownloadTask
            {
                Url = client.Url,
                TargetPath = clientJar,
                Sha1 = client.Sha1,
                Size = client.Size,
                Display = $"{detail.Id}.jar"
            });
        }
        else if (!File.Exists(clientJar))
        {
            throw new InvalidOperationException($"В JSON версии {detail.Id} нет ссылки на client.jar.");
        }

        // Библиотеки
        foreach (var lib in detail.Libraries)
        {
            if (!RuleEvaluator.Allows(lib.Rules)) continue;

            // Отсекаем natives чужой архитектуры (x86/arm64 на x64-системе и наоборот)
            if (RuleEvaluator.IsNativeArtifactName(lib.Name) &&
                !RuleEvaluator.NativeMatchesCurrentArch(lib.Name))
                continue;

            // 1) Основной артефакт
            var artifact = lib.Downloads?.Artifact;
            string? artifactPath = null;

            if (artifact is not null && !string.IsNullOrEmpty(artifact.Url))
            {
                var rel = artifact.Path ?? RuleEvaluator.MavenNameToPath(lib.Name);
                artifactPath = Path.Combine(Paths.LibrariesDir, rel.Replace('/', Path.DirectorySeparatorChar));
                tasks.Add(new DownloadTask
                {
                    Url = artifact.Url,
                    TargetPath = artifactPath,
                    Sha1 = artifact.Sha1,
                    Size = artifact.Size,
                    Display = Path.GetFileName(artifactPath)
                });
            }
            else if (lib.Downloads?.Classifiers is null && !string.IsNullOrEmpty(lib.Name))
            {
                // Библиотека без downloads-секции (форматы Forge/Fabric) — тянем с maven
                var rel = RuleEvaluator.MavenNameToPath(lib.Name);
                artifactPath = Path.Combine(Paths.LibrariesDir, rel.Replace('/', Path.DirectorySeparatorChar));
                var baseUrl = string.IsNullOrEmpty(lib.Url) ? LibrariesMirror : lib.Url!;
                if (!baseUrl.EndsWith('/')) baseUrl += "/";
                tasks.Add(new DownloadTask
                {
                    Url = baseUrl + rel,
                    TargetPath = artifactPath,
                    Display = Path.GetFileName(artifactPath)
                });
            }

            if (artifactPath is not null)
            {
                // Новый формат natives-* — не в classpath, а в распаковку
                if (RuleEvaluator.IsNativeArtifactName(lib.Name))
                    nativeArchives.Add((artifactPath, lib.Extract));
                else
                    classpath.Add(artifactPath);
            }

            // 2) Старый формат natives через classifiers
            var classifier = RuleEvaluator.GetNativeClassifier(lib);
            if (classifier is not null &&
                lib.Downloads?.Classifiers is not null &&
                lib.Downloads.Classifiers.TryGetValue(classifier, out var nativeDl) &&
                !string.IsNullOrEmpty(nativeDl.Url))
            {
                var rel = nativeDl.Path ?? RuleEvaluator.MavenNameToPath(lib.Name + ":" + classifier);
                var nativePath = Path.Combine(Paths.LibrariesDir, rel.Replace('/', Path.DirectorySeparatorChar));

                tasks.Add(new DownloadTask
                {
                    Url = nativeDl.Url,
                    TargetPath = nativePath,
                    Sha1 = nativeDl.Sha1,
                    Size = nativeDl.Size,
                    Display = Path.GetFileName(nativePath)
                });

                nativeArchives.Add((nativePath, lib.Extract));
            }
        }

        // log4j конфиг (нужен для -Dlog4j.configurationFile)
        var loggingFile = detail.Logging?.Client?.File;
        if (loggingFile is not null && !string.IsNullOrEmpty(loggingFile.Url))
        {
            var logPath = Path.Combine(Paths.LogConfigsDir, loggingFile.Id ?? "client.xml");
            tasks.Add(new DownloadTask
            {
                Url = loggingFile.Url!,
                TargetPath = logPath,
                Sha1 = loggingFile.Sha1,
                Size = loggingFile.Size,
                Display = Path.GetFileName(logPath)
            });
        }

        // --- 2. Качаем клиент + библиотеки -----------------------------
        await RunBatchAsync(tasks, "Загрузка клиента и библиотек", ct).ConfigureAwait(false);

        // --- 3. Распаковываем natives ----------------------------------
        ExtractNatives(nativeArchives, nativesExtractDir);

        // --- 4. Ассеты --------------------------------------------------
        var assetsVirtual = false;
        var assetIndexId = detail.AssetIndex?.Id ?? detail.Assets;

        if (detail.AssetIndex is not null)
            assetsVirtual = await DownloadAssetsAsync(detail.AssetIndex, ct).ConfigureAwait(false);

        classpath.Add(clientJar);

        Report(new DownloadProgress
        {
            Stage = "Готово",
            CurrentFile = "",
            BytesDone = 1, BytesTotal = 1,
            FilesDone = 1, FilesTotal = 1
        });

        return new InstallResult
        {
            Detail = detail,
            ClasspathJars = classpath.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ClientJar = clientJar,
            NativesDir = nativesDir,
            NativesExtractDir = nativesExtractDir,
            AssetsDir = assetsVirtual
                ? Path.Combine(Paths.AssetsVirtualDir, assetIndexId)
                : Paths.AssetsDir,
            AssetIndexId = assetIndexId,
            AssetsAreVirtual = assetsVirtual
        };
    }

    // =====================================================================
    //  АССЕТЫ
    // =====================================================================

    private async Task<bool> DownloadAssetsAsync(AssetIndexInfo indexInfo, CancellationToken ct)
    {
        Directory.CreateDirectory(Paths.AssetsIndexesDir);
        Directory.CreateDirectory(Paths.AssetsObjectsDir);

        var indexPath = Path.Combine(Paths.AssetsIndexesDir, indexInfo.Id + ".json");

        if (!await IsFileValidAsync(indexPath, indexInfo.Sha1, indexInfo.Size, ct).ConfigureAwait(false))
        {
            Report(new DownloadProgress { Stage = "Загрузка индекса ассетов", CurrentFile = indexInfo.Id + ".json" });
            await DownloadFileAsync(new DownloadTask
            {
                Url = indexInfo.Url,
                TargetPath = indexPath,
                Sha1 = indexInfo.Sha1,
                Size = indexInfo.Size,
                Display = indexInfo.Id + ".json"
            }, null, ct).ConfigureAwait(false);
        }

        var indexJson = await File.ReadAllTextAsync(indexPath, ct).ConfigureAwait(false);
        var index = JsonSerializer.Deserialize<AssetIndexFile>(indexJson)
                    ?? throw new InvalidOperationException("Не удалось разобрать индекс ассетов.");

        var assetTasks = new List<DownloadTask>(index.Objects.Count);

        foreach (var (name, obj) in index.Objects)
        {
            var target = Path.Combine(Paths.AssetsObjectsDir, obj.TwoLetterPrefix, obj.Hash);
            assetTasks.Add(new DownloadTask
            {
                Url = $"{ResourcesBase}/{obj.TwoLetterPrefix}/{obj.Hash}",
                TargetPath = target,
                Sha1 = obj.Hash,
                Size = obj.Size,
                Display = name
            });
        }

        await RunBatchAsync(assetTasks, "Загрузка ресурсов игры", ct).ConfigureAwait(false);

        // Старые версии (pre-1.7) требуют virtual/legacy; для 1.16.5+ обычно false,
        // но обрабатываем корректно на случай map_to_resources.
        if (index.Virtual || index.MapToResources)
        {
            var virtualDir = Path.Combine(Paths.AssetsVirtualDir, indexInfo.Id);
            Report(new DownloadProgress { Stage = "Подготовка виртуальных ассетов", CurrentFile = indexInfo.Id });

            foreach (var (name, obj) in index.Objects)
            {
                ct.ThrowIfCancellationRequested();
                var src = Path.Combine(Paths.AssetsObjectsDir, obj.TwoLetterPrefix, obj.Hash);
                var dst = Path.Combine(virtualDir, name.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(src)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (!File.Exists(dst) || new FileInfo(dst).Length != obj.Size)
                    File.Copy(src, dst, true);
            }
            return true;
        }

        return false;
    }

    // =====================================================================
    //  NATIVES
    // =====================================================================

    private static void ExtractNatives(List<(string archive, ExtractRule? extract)> archives, string nativesDir)
    {
        Directory.CreateDirectory(nativesDir);

        // Чистим библиотеки от прежних запусков: там могли остаться .dll
        // чужой разрядности, из-за которых LWJGL падает с UnsatisfiedLinkError.
        var stamp = Path.Combine(nativesDir, ".arch");
        var expected = RuleEvaluator.OsArch;

        if (!File.Exists(stamp) || File.ReadAllText(stamp).Trim() != expected)
        {
            // Удаляем только бинарники: подпапки jna/lwjgl/netty и служебные файлы не трогаем.
            foreach (var old in Directory.GetFiles(nativesDir))
            {
                var ext = Path.GetExtension(old).ToLowerInvariant();
                if (ext is not (".dll" or ".so" or ".dylib" or ".jnilib")) continue;
                try { File.Delete(old); } catch { /* занят игрой — пропускаем */ }
            }
            try { File.WriteAllText(stamp, expected); } catch { }
        }

        foreach (var (archive, extract) in archives)
        {
            if (!File.Exists(archive)) continue;

            try
            {
                using var zip = ZipFile.OpenRead(archive);
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // директория

                    var entryPath = entry.FullName.Replace('\\', '/');

                    if (entryPath.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (extract?.Exclude is not null &&
                        extract.Exclude.Any(x => entryPath.StartsWith(x.TrimStart('/'), StringComparison.OrdinalIgnoreCase)))
                        continue;

                    // Берём только бинарники для текущей платформы
                    var ext = Path.GetExtension(entryPath).ToLowerInvariant();
                    if (ext is not (".dll" or ".so" or ".dylib" or ".jnilib")) continue;

                    var dest = Path.Combine(nativesDir, Path.GetFileName(entryPath));
                    if (File.Exists(dest) && new FileInfo(dest).Length == entry.Length) continue;

                    Directory.CreateDirectory(nativesDir);
                    entry.ExtractToFile(dest, true);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Не удалось распаковать natives из {Path.GetFileName(archive)}: {ex.Message}");
            }
        }
    }

    // =====================================================================
    //  ДВИЖОК ЗАГРУЗКИ
    // =====================================================================

    private async Task RunBatchAsync(List<DownloadTask> tasks, string stage, CancellationToken ct)
    {
        // Отсеиваем то, что уже валидно лежит на диске
        var pending = new ConcurrentBag<DownloadTask>();

        await Parallel.ForEachAsync(tasks,
            new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = ct },
            async (t, token) =>
            {
                if (!await IsFileValidAsync(t.TargetPath, t.Sha1, t.Size, token).ConfigureAwait(false))
                    pending.Add(t);
            }).ConfigureAwait(false);

        var list = pending.ToList();
        if (list.Count == 0)
        {
            Report(new DownloadProgress
            {
                Stage = stage, CurrentFile = "всё уже загружено",
                BytesDone = 1, BytesTotal = 1, FilesDone = 1, FilesTotal = 1
            });
            return;
        }

        long totalBytes = list.Sum(t => t.Size > 0 ? t.Size : 32 * 1024);
        long doneBytes = 0;
        int doneFiles = 0;
        var total = list.Count;
        var currentFile = "";

        Report(new DownloadProgress
        {
            Stage = stage, CurrentFile = "подготовка...",
            BytesTotal = totalBytes, FilesTotal = total
        });

        await Parallel.ForEachAsync(list,
            new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = ct },
            async (task, token) =>
            {
                Volatile.Write(ref currentFile, task.Display);

                await DownloadFileAsync(task, delta =>
                {
                    var d = Interlocked.Add(ref doneBytes, delta);
                    Report(new DownloadProgress
                    {
                        Stage = stage,
                        CurrentFile = Volatile.Read(ref currentFile),
                        BytesDone = d,
                        BytesTotal = totalBytes,
                        FilesDone = Volatile.Read(ref doneFiles),
                        FilesTotal = total
                    });
                }, token).ConfigureAwait(false);

                var f = Interlocked.Increment(ref doneFiles);
                Report(new DownloadProgress
                {
                    Stage = stage,
                    CurrentFile = task.Display,
                    BytesDone = Volatile.Read(ref doneBytes),
                    BytesTotal = totalBytes,
                    FilesDone = f,
                    FilesTotal = total
                });
            }).ConfigureAwait(false);
    }

    private async Task DownloadFileAsync(DownloadTask task, Action<long>? onBytes, CancellationToken ct)
    {
        const int maxAttempts = 4;
        Exception? last = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(task.TargetPath)!);
                var tmp = task.TargetPath + ".part";

                using (var resp = await _http.GetAsync(task.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                           .ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();

                    await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                        81920, useAsync: true);

                    var buffer = new byte[81920];
                    int read;
                    while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        onBytes?.Invoke(read);
                    }
                }

                if (!string.IsNullOrEmpty(task.Sha1))
                {
                    var actual = await ComputeSha1Async(tmp, ct).ConfigureAwait(false);
                    if (!string.Equals(actual, task.Sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(tmp);
                        throw new IOException($"SHA1 не совпал для {task.Display}");
                    }
                }

                if (File.Exists(task.TargetPath)) File.Delete(task.TargetPath);
                File.Move(tmp, task.TargetPath);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                if (attempt < maxAttempts)
                    await Task.Delay(400 * attempt, ct).ConfigureAwait(false);
            }
        }

        throw new IOException($"Не удалось скачать {task.Display} ({task.Url}): {last?.Message}", last);
    }

    private static async Task<bool> IsFileValidAsync(string path, string? sha1, long size, CancellationToken ct)
    {
        if (!File.Exists(path)) return false;

        var fi = new FileInfo(path);
        if (size > 0 && fi.Length != size) return false;
        if (fi.Length == 0) return false;

        if (string.IsNullOrEmpty(sha1)) return true;

        // Для больших файлов доверяем размеру, если хэш уже проверялся ранее (маркер .ok)
        var marker = path + ".ok";
        if (File.Exists(marker)) return true;

        var actual = await ComputeSha1Async(path, ct).ConfigureAwait(false);
        var ok = string.Equals(actual, sha1, StringComparison.OrdinalIgnoreCase);

        if (ok && fi.Length > 4 * 1024 * 1024)
        {
            try { await File.WriteAllTextAsync(marker, actual, ct).ConfigureAwait(false); } catch { }
        }

        return ok;
    }

    private static async Task<string> ComputeSha1Async(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var sha = SHA1.Create();
        var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void Report(DownloadProgress p) => Progress?.Invoke(p);
}
