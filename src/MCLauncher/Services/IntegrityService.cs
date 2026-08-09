using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MCLauncher.Models;

namespace MCLauncher.Services;

/// <summary>Результат проверки сборки.</summary>
public sealed class IntegrityReport
{
    public List<string> Ok { get; } = new();
    public List<string> Problems { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Fixable { get; } = new();

    public bool IsHealthy => Problems.Count == 0;

    public string Summary => Problems.Count == 0 && Warnings.Count == 0
        ? "Сборка в порядке — всё на месте."
        : $"Проблем: {Problems.Count}, предупреждений: {Warnings.Count}";
}

/// <summary>
/// Проверка сборки одной кнопкой: на месте ли клиент, библиотеки,
/// нативные файлы, не побиты ли моды.
/// </summary>
public sealed class IntegrityService
{
    private readonly VersionService _versions;

    public IntegrityService(VersionService versions) => _versions = versions;

    public event Action<string>? Status;

    public async Task<IntegrityReport> CheckAsync(GameInstance inst, CancellationToken ct = default)
    {
        var report = new IntegrityReport();
        var paths = GamePaths.ForInstance(inst);

        Status?.Invoke("Проверяю файлы версии...");

        // ---------- 1. Клиентский JAR ----------
        var jar = paths.VersionJar(inst.McVersion);
        if (File.Exists(jar))
        {
            var size = new FileInfo(jar).Length;
            if (size < 1024 * 1024)
            {
                report.Problems.Add($"client.jar подозрительно мал ({size / 1024} КБ) — вероятно, битый");
                report.Fixable.Add("client");
            }
            else if (!IsValidZip(jar))
            {
                report.Problems.Add("client.jar повреждён (не открывается как архив)");
                report.Fixable.Add("client");
            }
            else report.Ok.Add($"client.jar на месте ({size / 1048576} МБ)");
        }
        else
        {
            report.Problems.Add($"Нет client.jar для {inst.McVersion}");
            report.Fixable.Add("client");
        }

        // ---------- 2. Профиль версии ----------
        var launchId = inst.EffectiveVersionId;
        var json = paths.VersionJson(launchId);

        if (!File.Exists(json))
        {
            report.Problems.Add($"Нет описания версии {launchId}");
            report.Fixable.Add("version");
        }
        else
        {
            try
            {
                var detail = await _versions.ResolveAsync(launchId, ct).ConfigureAwait(false);
                report.Ok.Add($"Профиль версии читается ({detail.Libraries.Count} библиотек)");

                // ---------- 3. Библиотеки ----------
                Status?.Invoke("Проверяю библиотеки...");

                var missing = 0;
                var checkedCount = 0;

                foreach (var lib in detail.Libraries)
                {
                    ct.ThrowIfCancellationRequested();

                    if (!RuleEvaluator.Allows(lib.Rules)) continue;
                    if (RuleEvaluator.IsNativeArtifactName(lib.Name) &&
                        !RuleEvaluator.NativeMatchesCurrentArch(lib.Name)) continue;

                    var rel = lib.Downloads?.Artifact?.Path;
                    if (rel is null)
                    {
                        try { rel = RuleEvaluator.MavenNameToPath(lib.Name); }
                        catch { continue; }
                    }

                    var path = Path.Combine(paths.LibrariesDir,
                        rel.Replace('/', Path.DirectorySeparatorChar));

                    checkedCount++;
                    if (!File.Exists(path)) missing++;
                }

                if (missing > 0)
                {
                    report.Problems.Add($"Не хватает библиотек: {missing} из {checkedCount}");
                    report.Fixable.Add("libraries");
                }
                else report.Ok.Add($"Библиотеки на месте ({checkedCount})");

                // ---------- 4. Нативные файлы ----------
                var nativesDir = DownloadManager.ResolveNativesExtractDir(
                    detail, paths.NativesDir(launchId));

                if (Directory.Exists(nativesDir))
                {
                    var dlls = Directory.GetFiles(nativesDir, "*.dll").Length;
                    if (dlls == 0)
                    {
                        report.Problems.Add("Нативные библиотеки не распакованы (папка пуста)");
                        report.Fixable.Add("natives");
                    }
                    else if (!File.Exists(Path.Combine(nativesDir, "lwjgl.dll")))
                    {
                        report.Problems.Add("Нет lwjgl.dll — игра не запустится");
                        report.Fixable.Add("natives");
                    }
                    else report.Ok.Add($"Нативные библиотеки на месте ({dlls} файлов)");
                }
                else
                {
                    report.Problems.Add("Папка natives отсутствует");
                    report.Fixable.Add("natives");
                }

                // ---------- 5. Ресурсы ----------
                Status?.Invoke("Проверяю ресурсы игры...");

                if (detail.AssetIndex is not null)
                {
                    var indexPath = Path.Combine(paths.AssetsIndexesDir, detail.AssetIndex.Id + ".json");

                    if (!File.Exists(indexPath))
                    {
                        report.Problems.Add($"Нет индекса ресурсов {detail.AssetIndex.Id}");
                        report.Fixable.Add("assets");
                    }
                    else
                    {
                        try
                        {
                            var idx = JsonSerializer.Deserialize<AssetIndexFile>(
                                await File.ReadAllTextAsync(indexPath, ct).ConfigureAwait(false));

                            if (idx is not null)
                            {
                                // Полная проверка тысяч файлов слишком долгая — берём выборку
                                var sample = idx.Objects.Values.Take(150).ToList();
                                var lost = sample.Count(o => !File.Exists(
                                    Path.Combine(paths.AssetsObjectsDir, o.TwoLetterPrefix, o.Hash)));

                                if (lost > 0)
                                {
                                    var percent = lost * 100 / Math.Max(1, sample.Count);
                                    report.Problems.Add(
                                        $"Потеряно примерно {percent}% ресурсов (звуки, языки)");
                                    report.Fixable.Add("assets");
                                }
                                else report.Ok.Add($"Ресурсы на месте (проверено {sample.Count} из {idx.Objects.Count})");
                            }
                        }
                        catch (Exception ex)
                        {
                            report.Warnings.Add("Индекс ресурсов не читается: " + ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                report.Problems.Add("Профиль версии повреждён: " + ex.Message);
                report.Fixable.Add("version");
            }
        }

        // ---------- 6. Моды ----------
        Status?.Invoke("Проверяю моды...");

        var modsDir = InstanceService.ModsDir(inst);
        if (Directory.Exists(modsDir))
        {
            var jars = Directory.GetFiles(modsDir, "*.jar");
            var broken = jars.Where(f => !IsValidZip(f)).ToList();

            if (broken.Count > 0)
                report.Problems.Add($"Повреждённые моды: {string.Join(", ", broken.Select(Path.GetFileName))}");
            else if (jars.Length > 0)
                report.Ok.Add($"Моды читаются ({jars.Length} шт.)");

            if (jars.Length > 0 && inst.Loader == LoaderKind.Vanilla)
                report.Warnings.Add(
                    $"В сборке {jars.Length} мод(ов), но загрузчик не установлен — они не запустятся");

            var conflicts = ModInspector.FindConflicts(
                ModInspector.ReadAll(modsDir), inst.Loader);

            foreach (var c in conflicts.Where(x => x.IsError))
                report.Problems.Add(c.Title + " — " + c.Details);

            foreach (var c in conflicts.Where(x => !x.IsError))
                report.Warnings.Add(c.Title);
        }

        // ---------- 7. Java ----------
        var required = JavaService.RequiredJavaFor(inst.McVersion);
        if (!string.IsNullOrWhiteSpace(inst.JavaPath))
        {
            if (!File.Exists(inst.JavaPath))
                report.Problems.Add("Указанный для сборки java.exe не найден");
            else
            {
                var probe = JavaService.Probe(inst.JavaPath, "check");
                if (probe is null) report.Problems.Add("Указанный java.exe не запускается");
                else if (probe.MajorVersion < required)
                    report.Warnings.Add(
                        $"Выбрана Java {probe.MajorVersion}, для {inst.McVersion} нужна {required}");
                else report.Ok.Add($"Java {probe.MajorVersion} подходит");
            }
        }

        // ---------- 8. Свободное место ----------
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(LauncherPaths.Root)!);
            var freeGb = drive.AvailableFreeSpace / 1073741824.0;

            if (freeGb < 1) report.Problems.Add($"На диске меньше 1 ГБ свободно ({freeGb:0.#} ГБ)");
            else if (freeGb < 3) report.Warnings.Add($"Мало места на диске: {freeGb:0.#} ГБ");
        }
        catch { }

        Status?.Invoke(report.Summary);
        return report;
    }

    private static bool IsValidZip(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.Entries.Count > 0;
        }
        catch { return false; }
    }

    /// <summary>Удаляет битые файлы, чтобы загрузчик скачал их заново.</summary>
    public static int Repair(GameInstance inst, IntegrityReport report)
    {
        var removed = 0;
        var paths = GamePaths.ForInstance(inst);

        try
        {
            if (report.Fixable.Contains("client"))
            {
                var jar = paths.VersionJar(inst.McVersion);
                if (File.Exists(jar)) { File.Delete(jar); removed++; }

                var ok = jar + ".ok";
                if (File.Exists(ok)) File.Delete(ok);
            }

            if (report.Fixable.Contains("natives"))
            {
                var dir = paths.NativesDir(inst.EffectiveVersionId);
                if (Directory.Exists(dir)) { Directory.Delete(dir, true); removed++; }
            }

            if (report.Fixable.Contains("version"))
            {
                var dir = paths.VersionDir(inst.EffectiveVersionId);
                if (Directory.Exists(dir)) { Directory.Delete(dir, true); removed++; }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Восстановление: " + ex.Message);
        }

        return removed;
    }
}
