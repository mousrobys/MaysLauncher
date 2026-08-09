using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MCLauncher.Services;

public sealed class JavaInstallation
{
    public required string JavaExe { get; init; }   // javaw.exe для запуска без консоли
    public required string JavaConsoleExe { get; init; }
    public required int MajorVersion { get; init; }
    public required string DisplayVersion { get; init; }
    public string Source { get; init; } = "system";

    public override string ToString() => $"Java {DisplayVersion} ({Source}) — {JavaExe}";
}

/// <summary>
/// Поиск установленной Java нужной мажорной версии,
/// а при отсутствии — автоматическая загрузка портативного JRE от Eclipse Adoptium.
/// </summary>
public sealed class JavaService
{
    private readonly HttpClient _http;

    public JavaService(HttpClient http) => _http = http;

    public event Action<DownloadProgress>? Progress;

    /// <summary>Требуемая мажорная версия Java по версии Minecraft (fallback, если нет javaVersion в JSON).</summary>
    public static int RequiredJavaFor(string mcVersionId)
    {
        var v = VersionService.ParseMcVersion(mcVersionId);
        if (v is null) return 21;
        if (v >= new Version(1, 20, 5)) return 21;
        if (v >= new Version(1, 18, 0)) return 17;
        if (v >= new Version(1, 17, 0)) return 16;
        return 8;
    }

    // =====================================================================
    //  ПОИСК
    // =====================================================================

    public List<JavaInstallation> FindAll()
    {
        var found = new Dictionary<string, JavaInstallation>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string javaHomeOrBin, string source)
        {
            var exe = ResolveJavaExe(javaHomeOrBin);
            if (exe is null) return;
            if (found.ContainsKey(exe)) return;

            var info = Probe(exe, source);
            if (info is not null) found[exe] = info;
        }

        // 1. Ранее скачанные лаунчером рантаймы
        if (Directory.Exists(LauncherPaths.RuntimeDir))
        {
            foreach (var dir in Directory.GetDirectories(LauncherPaths.RuntimeDir))
            {
                // внутри может быть вложенная папка jdk-XX
                TryAdd(dir, "runtime");
                foreach (var sub in Directory.GetDirectories(dir))
                    TryAdd(sub, "runtime");
            }
        }

        // 2. JAVA_HOME
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome)) TryAdd(javaHome!, "JAVA_HOME");

        // 3. PATH
        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try
            {
                if (File.Exists(Path.Combine(p, "java.exe"))) TryAdd(p, "PATH");
            }
            catch { }
        }

        // 4. Типовые каталоги установки
        foreach (var root in EnumerateCommonRoots())
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                    TryAdd(dir, "installed");
            }
            catch { }
        }

        // 5. Реестр
        if (OperatingSystem.IsWindows()) ScanRegistry(TryAdd);

        // 6. Официальный лаунчер Minecraft (его встроенные JRE)
        var mcRuntime = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages");
        var mojangRuntime = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".minecraft", "runtime");
        if (Directory.Exists(mojangRuntime))
        {
            try
            {
                foreach (var comp in Directory.GetDirectories(mojangRuntime))
                foreach (var arch in Directory.GetDirectories(comp))
                foreach (var jdk in Directory.GetDirectories(arch))
                    TryAdd(jdk, "mojang");
            }
            catch { }
        }
        _ = mcRuntime;

        return found.Values.OrderByDescending(j => j.MajorVersion).ToList();
    }

    private static IEnumerable<string> EnumerateCommonRoots()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var b in new[] { pf, pf86 })
        {
            if (string.IsNullOrEmpty(b)) continue;
            yield return Path.Combine(b, "Java");
            yield return Path.Combine(b, "Eclipse Adoptium");
            yield return Path.Combine(b, "AdoptOpenJDK");
            yield return Path.Combine(b, "Amazon Corretto");
            yield return Path.Combine(b, "Microsoft");
            yield return Path.Combine(b, "Zulu");
            yield return Path.Combine(b, "BellSoft");
            yield return Path.Combine(b, "Semeru");
            yield return Path.Combine(b, "RedHat");
        }

        yield return Path.Combine(local, "Programs", "Eclipse Adoptium");
        yield return @"C:\Java";
    }

    private static void ScanRegistry(Action<string, string> tryAdd)
    {
        string[] keys =
        {
            @"SOFTWARE\JavaSoft\JDK",
            @"SOFTWARE\JavaSoft\JRE",
            @"SOFTWARE\JavaSoft\Java Development Kit",
            @"SOFTWARE\JavaSoft\Java Runtime Environment",
            @"SOFTWARE\Eclipse Adoptium\JDK",
            @"SOFTWARE\Eclipse Foundation\JDK",
            @"SOFTWARE\Microsoft\JDK",
            @"SOFTWARE\Azul Systems\Zulu"
        };

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            foreach (var keyPath in keys)
            {
                try
                {
                    using var key = baseKey.OpenSubKey(keyPath);
                    if (key is null) continue;

                    foreach (var sub in key.GetSubKeyNames())
                    {
                        using var vk = key.OpenSubKey(sub);
                        if (vk is null) continue;

                        var home = vk.GetValue("JavaHome") as string;
                        if (!string.IsNullOrEmpty(home)) { tryAdd(home!, "registry"); continue; }

                        using var hk = vk.OpenSubKey("hotspot\\MSI");
                        var path = hk?.GetValue("Path") as string;
                        if (!string.IsNullOrEmpty(path)) tryAdd(path!, "registry");
                    }
                }
                catch { }
            }
        }
    }

    private static string? ResolveJavaExe(string dir)
    {
        try
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(dir, "bin", "java.exe"),
                         Path.Combine(dir, "java.exe")
                     })
            {
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { }
        return null;
    }

    public static JavaInstallation? Probe(string javaExe, string source = "manual")
    {
        try
        {
            var psi = new ProcessStartInfo(javaExe, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p is null) return null;

            var output = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);

            var m = Regex.Match(output, @"version\s+""(?<v>[\d._]+)(?:-[\w.]+)?""");
            if (!m.Success) return null;

            var raw = m.Groups["v"].Value;
            var parts = raw.Split('.', '_');
            var major = int.Parse(parts[0]);
            if (major == 1 && parts.Length > 1) major = int.Parse(parts[1]); // 1.8.0_301 -> 8

            var dir = Path.GetDirectoryName(javaExe)!;
            var javaw = Path.Combine(dir, "javaw.exe");

            return new JavaInstallation
            {
                JavaExe = File.Exists(javaw) ? javaw : javaExe,
                JavaConsoleExe = javaExe,
                MajorVersion = major,
                DisplayVersion = raw,
                Source = source
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Возвращает подходящую Java или скачивает её, если ничего не найдено.</summary>
    public async Task<JavaInstallation> EnsureJavaAsync(int requiredMajor, CancellationToken ct = default)
    {
        var all = FindAll();

        // Точное совпадение приоритетнее
        var exact = all.FirstOrDefault(j => j.MajorVersion == requiredMajor);
        if (exact is not null)
        {
            Log.Info($"Найдена {exact}");
            return exact;
        }

        // Более новая тоже подойдёт (кроме MC <=1.16, где нужна именно 8..16)
        var newer = all.Where(j => j.MajorVersion > requiredMajor)
                       .OrderBy(j => j.MajorVersion)
                       .FirstOrDefault();

        if (newer is not null && requiredMajor >= 17)
        {
            Log.Info($"Использую более новую {newer}");
            return newer;
        }

        if (newer is not null && requiredMajor == 8 && newer.MajorVersion <= 11)
            return newer;

        Log.Info($"Java {requiredMajor} не найдена — скачиваю портативную сборку Adoptium...");
        return await DownloadAdoptiumAsync(requiredMajor, ct).ConfigureAwait(false);
    }

    // =====================================================================
    //  АВТОЗАГРУЗКА JRE
    // =====================================================================

    public async Task<JavaInstallation> DownloadAdoptiumAsync(int major, CancellationToken ct)
    {
        var arch = Environment.Is64BitOperatingSystem
            ? (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
               System.Runtime.InteropServices.Architecture.Arm64 ? "aarch64" : "x64")
            : "x86";

        var apiUrl = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot" +
                     $"?architecture={arch}&image_type=jre&os=windows&vendor=eclipse";

        Progress?.Invoke(new DownloadProgress { Stage = $"Поиск Java {major}", CurrentFile = "api.adoptium.net" });

        string? downloadUrl = null;
        long downloadSize = 0;

        try
        {
            var json = await _http.GetStringAsync(apiUrl, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            foreach (var asset in doc.RootElement.EnumerateArray())
            {
                if (!asset.TryGetProperty("binary", out var bin)) continue;
                if (!bin.TryGetProperty("package", out var pkg)) continue;

                var link = pkg.GetProperty("link").GetString();
                if (link is null || !link.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

                downloadUrl = link;
                downloadSize = pkg.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("Adoptium API недоступен: " + ex.Message);
        }

        if (downloadUrl is null)
            throw new InvalidOperationException(
                $"Не удалось автоматически получить Java {major}. " +
                $"Установите её вручную: https://adoptium.net/temurin/releases/?version={major}");

        var targetRoot = Path.Combine(LauncherPaths.RuntimeDir, $"jre-{major}");
        Directory.CreateDirectory(LauncherPaths.RuntimeDir);

        var zipPath = Path.Combine(LauncherPaths.CacheDir, $"jre-{major}.zip");
        Directory.CreateDirectory(LauncherPaths.CacheDir);

        // Скачивание с прогрессом
        using (var resp = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                   .ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? downloadSize;

            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                Progress?.Invoke(new DownloadProgress
                {
                    Stage = $"Загрузка Java {major}",
                    CurrentFile = Path.GetFileName(downloadUrl),
                    BytesDone = done,
                    BytesTotal = total
                });
            }
        }

        Progress?.Invoke(new DownloadProgress
        {
            Stage = $"Распаковка Java {major}", CurrentFile = "…", BytesDone = 0, BytesTotal = 0
        });

        if (Directory.Exists(targetRoot))
        {
            try { Directory.Delete(targetRoot, true); } catch { }
        }
        Directory.CreateDirectory(targetRoot);

        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, targetRoot, true), ct).ConfigureAwait(false);
        try { File.Delete(zipPath); } catch { }

        // Внутри архива обычно одна папка jdk-XX-jre
        var javaExe = Directory.GetFiles(targetRoot, "java.exe", SearchOption.AllDirectories)
                               .FirstOrDefault(f => f.Contains(Path.Combine("bin", "java.exe"), StringComparison.OrdinalIgnoreCase));

        if (javaExe is null)
            throw new InvalidOperationException("В скачанном архиве Java не найден java.exe.");

        var result = Probe(javaExe, "runtime")
                     ?? throw new InvalidOperationException("Скачанная Java не запускается.");

        Log.Info($"Java установлена: {result}");
        return result;
    }
}
