using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using MCLauncher.Models;

namespace MCLauncher.Services;

/// <summary>
/// Установка модлоадеров Fabric, Forge и NeoForge.
/// Fabric ставится напрямую из meta.fabricmc.net (готовый профиль JSON).
/// Forge/NeoForge устанавливаются официальным installer.jar в headless-режиме.
/// </summary>
public sealed class ModLoaderService
{
    private const string FabricMetaBase = "https://meta.fabricmc.net/v2";
    private const string ForgeMetadata = "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";
    private const string ForgeInstallerBase = "https://maven.minecraftforge.net/net/minecraftforge/forge";
    private const string NeoForgeMetadata = "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";
    private const string NeoForgeInstallerBase = "https://maven.neoforged.net/releases/net/neoforged/neoforge";

    private readonly HttpClient _http;

    public ModLoaderService(HttpClient http) => _http = http;

    /// <summary>Хранилище версий (общее либо изолированное).</summary>
    public GamePaths Paths { get; set; } = GamePaths.Shared;

    /// <summary>
    /// Корень, куда Forge/NeoForge installer ставит клиент (аналог .minecraft).
    /// Для изолированной сборки — её собственная папка.
    /// </summary>
    public string InstallRoot { get; set; } = LauncherPaths.Root;

    public event Action<string>? Status;
    private void Report(string s) { Status?.Invoke(s); Log.Info(s); }

    // =====================================================================
    //  СПИСКИ ВЕРСИЙ
    // =====================================================================

    public async Task<List<LoaderVersion>> GetLoaderVersionsAsync(
        LoaderKind kind, string mcVersion, CancellationToken ct = default)
    {
        return kind switch
        {
            LoaderKind.Fabric => await GetFabricVersionsAsync(mcVersion, ct).ConfigureAwait(false),
            LoaderKind.Forge => await GetForgeVersionsAsync(mcVersion, ct).ConfigureAwait(false),
            LoaderKind.NeoForge => await GetNeoForgeVersionsAsync(mcVersion, ct).ConfigureAwait(false),
            _ => new List<LoaderVersion>()
        };
    }

    private async Task<List<LoaderVersion>> GetFabricVersionsAsync(string mcVersion, CancellationToken ct)
    {
        var result = new List<LoaderVersion>();

        var json = await _http.GetStringAsync($"{FabricMetaBase}/versions/loader/{Uri.EscapeDataString(mcVersion)}", ct)
            .ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var first = true;

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("loader", out var loader)) continue;

            var version = loader.GetProperty("version").GetString();
            if (string.IsNullOrEmpty(version)) continue;

            var stable = loader.TryGetProperty("stable", out var st) && st.GetBoolean();

            result.Add(new LoaderVersion
            {
                Kind = LoaderKind.Fabric,
                Version = version!,
                McVersion = mcVersion,
                IsStable = stable,
                IsRecommended = first
            });
            first = false;
        }

        return result;
    }

    private async Task<List<LoaderVersion>> GetForgeVersionsAsync(string mcVersion, CancellationToken ct)
    {
        var all = await FetchMavenVersionsAsync(ForgeMetadata, ct).ConfigureAwait(false);

        // Формат: "1.20.1-47.2.0" (иногда с суффиксом ветки)
        var prefix = mcVersion + "-";
        var matched = all.Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
                         .Reverse()
                         .ToList();

        return matched.Select((v, i) => new LoaderVersion
        {
            Kind = LoaderKind.Forge,
            Version = v,
            McVersion = mcVersion,
            IsStable = true,
            IsRecommended = i == 0
        }).ToList();
    }

    private async Task<List<LoaderVersion>> GetNeoForgeVersionsAsync(string mcVersion, CancellationToken ct)
    {
        var all = await FetchMavenVersionsAsync(NeoForgeMetadata, ct).ConfigureAwait(false);

        // NeoForge для 1.20.1 использует старую схему "1.20.1-47.x",
        // с 1.20.2 — "<minor>.<patch>.<build>" (напр. 21.4.10 для MC 1.21.4).
        var mv = VersionService.ParseMcVersion(mcVersion);
        List<string> matched;

        if (mv is not null && mv >= new Version(1, 20, 2))
        {
            var prefix = $"{mv.Minor}.{mv.Build}.";
            matched = all.Where(v => v.StartsWith(prefix, StringComparison.Ordinal)).Reverse().ToList();
        }
        else
        {
            var prefix = mcVersion + "-";
            matched = all.Where(v => v.StartsWith(prefix, StringComparison.Ordinal)).Reverse().ToList();
        }

        return matched.Select((v, i) => new LoaderVersion
        {
            Kind = LoaderKind.NeoForge,
            Version = v,
            McVersion = mcVersion,
            IsStable = !v.Contains("beta", StringComparison.OrdinalIgnoreCase),
            IsRecommended = i == 0
        }).ToList();
    }

    private async Task<List<string>> FetchMavenVersionsAsync(string metadataUrl, CancellationToken ct)
    {
        var xml = await _http.GetStringAsync(metadataUrl, ct).ConfigureAwait(false);
        var doc = XDocument.Parse(xml);

        return doc.Root?
            .Element("versioning")?
            .Element("versions")?
            .Elements("version")
            .Select(e => e.Value)
            .ToList() ?? new List<string>();
    }

    /// <summary>Список версий Minecraft, для которых существует данный загрузчик.</summary>
    public async Task<HashSet<string>> GetSupportedMcVersionsAsync(LoaderKind kind, CancellationToken ct = default)
    {
        try
        {
            switch (kind)
            {
                case LoaderKind.Fabric:
                {
                    var json = await _http.GetStringAsync($"{FabricMetaBase}/versions/game", ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    return doc.RootElement.EnumerateArray()
                        .Select(e => e.GetProperty("version").GetString() ?? "")
                        .Where(s => s.Length > 0)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                case LoaderKind.Forge:
                {
                    var all = await FetchMavenVersionsAsync(ForgeMetadata, ct).ConfigureAwait(false);
                    return all.Select(v => v.Split('-')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                case LoaderKind.NeoForge:
                {
                    var all = await FetchMavenVersionsAsync(NeoForgeMetadata, ct).ConfigureAwait(false);
                    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var v in all)
                    {
                        if (v.Contains('-') && v.StartsWith('1')) { set.Add(v.Split('-')[0]); continue; }
                        var parts = v.Split('.');
                        if (parts.Length >= 2) set.Add($"1.{parts[0]}.{parts[1]}");
                    }
                    return set;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Не удалось получить список версий {kind}: {ex.Message}");
        }

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    // =====================================================================
    //  УСТАНОВКА
    // =====================================================================

    /// <summary>
    /// Устанавливает загрузчик и возвращает id версии для запуска
    /// (например "fabric-loader-0.16.9-1.21.4").
    /// </summary>
    public async Task<string> InstallAsync(
        LoaderKind kind, string mcVersion, string loaderVersion,
        JavaInstallation java, CancellationToken ct = default)
    {
        return kind switch
        {
            LoaderKind.Fabric => await InstallFabricAsync(mcVersion, loaderVersion, ct).ConfigureAwait(false),
            LoaderKind.Forge => await InstallWithInstallerAsync(
                LoaderKind.Forge, mcVersion, loaderVersion, java, ct).ConfigureAwait(false),
            LoaderKind.NeoForge => await InstallWithInstallerAsync(
                LoaderKind.NeoForge, mcVersion, loaderVersion, java, ct).ConfigureAwait(false),
            _ => mcVersion
        };
    }

    // ---------------- Fabric ----------------

    private async Task<string> InstallFabricAsync(string mcVersion, string loaderVersion, CancellationToken ct)
    {
        Report($"Устанавливаю Fabric {loaderVersion} для {mcVersion}...");

        var url = $"{FabricMetaBase}/versions/loader/" +
                  $"{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json";

        var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var versionId = doc.RootElement.GetProperty("id").GetString()
                        ?? $"fabric-loader-{loaderVersion}-{mcVersion}";

        var dir = Paths.VersionDir(versionId);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Paths.VersionJson(versionId), json, ct).ConfigureAwait(false);

        Report($"Fabric установлен: {versionId}");
        return versionId;
    }

    // ---------------- Forge / NeoForge ----------------

    private async Task<string> InstallWithInstallerAsync(
        LoaderKind kind, string mcVersion, string loaderVersion,
        JavaInstallation java, CancellationToken ct)
    {
        var name = kind.Display();
        Report($"Загружаю установщик {name} {loaderVersion}...");

        var (installerUrl, expectedId) = kind == LoaderKind.Forge
            ? ($"{ForgeInstallerBase}/{loaderVersion}/forge-{loaderVersion}-installer.jar",
                $"{mcVersion}-forge-{loaderVersion[(loaderVersion.IndexOf('-') + 1)..]}")
            : ($"{NeoForgeInstallerBase}/{loaderVersion}/neoforge-{loaderVersion}-installer.jar",
                $"neoforge-{loaderVersion}");

        // Уже установлено?
        var existing = FindInstalledVersion(kind, mcVersion, loaderVersion);
        if (existing is not null)
        {
            Report($"{name} {loaderVersion} уже установлен ({existing}).");
            return existing;
        }

        Directory.CreateDirectory(LauncherPaths.CacheDir);
        var installerPath = Path.Combine(LauncherPaths.CacheDir, $"{kind}-{loaderVersion}-installer.jar");

        using (var resp = await _http.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                   .ConfigureAwait(false))
        {
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Не удалось скачать установщик {name} {loaderVersion} (HTTP {(int)resp.StatusCode}). " +
                    "Возможно, эта версия недоступна для выбранной версии игры.");

            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None,
                81920, true);
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }

        // Установщику нужен launcher_profiles.json в папке игры
        EnsureLauncherProfiles();

        Report($"Запускаю установщик {name} (это может занять до минуты)...");

        if (!File.Exists(java.JavaConsoleExe))
        {
            throw new FileNotFoundException(
                $"Java не найдена: {java.JavaConsoleExe}. Установите Java {JavaService.RequiredJavaFor(mcVersion)}+ или скачайте через лаунчер.");
        }

        var psi = new ProcessStartInfo(java.JavaConsoleExe)
        {
            WorkingDirectory = InstallRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installerPath);
        psi.ArgumentList.Add("--installClient");
        psi.ArgumentList.Add(InstallRoot);

        using var proc = new Process { StartInfo = psi };
        var output = new System.Text.StringBuilder();

        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Не удалось запустить установщик: {ex.Message}. Java: {java.JavaConsoleExe}");
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var finished = await Task.Run(() => proc.WaitForExit(300_000), ct).ConfigureAwait(false);
        if (!finished)
        {
            try { proc.Kill(true); } catch { }
            throw new TimeoutException($"Установщик {name} не завершился за 5 минут.");
        }

        Log.Info($"Вывод установщика {name}:\n" + output);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"Установщик {name} завершился с кодом {proc.ExitCode}. Подробности в launcher.log.");

        try { File.Delete(installerPath); } catch { }

        var installed = FindInstalledVersion(kind, mcVersion, loaderVersion)
                        ?? (Directory.Exists(Paths.VersionDir(expectedId)) ? expectedId : null);

        if (installed is null)
            throw new InvalidOperationException(
                $"{name} установлен, но профиль версии не найден. Проверьте папку versions.");

        Report($"{name} установлен: {installed}");
        return installed;
    }

    /// <summary>Ищет уже установленный профиль загрузчика среди папок versions.</summary>
    private string? FindInstalledVersion(LoaderKind kind, string mcVersion, string loaderVersion)
    {
        if (!Directory.Exists(Paths.VersionsDir)) return null;

        // Короткая часть версии: "1.20.1-47.2.0" -> "47.2.0"
        var shortVer = loaderVersion.Contains('-')
            ? loaderVersion[(loaderVersion.IndexOf('-') + 1)..]
            : loaderVersion;

        var token = kind == LoaderKind.Forge ? "forge" : "neoforge";

        foreach (var dir in Directory.GetDirectories(Paths.VersionsDir))
        {
            var id = Path.GetFileName(dir);
            if (!File.Exists(Path.Combine(dir, id + ".json"))) continue;

            var lower = id.ToLowerInvariant();
            if (!lower.Contains(token, StringComparison.Ordinal)) continue;
            if (kind == LoaderKind.Forge && lower.Contains("neoforge", StringComparison.Ordinal)) continue;
            if (!lower.Contains(shortVer.ToLowerInvariant(), StringComparison.Ordinal)) continue;

            return id;
        }

        return null;
    }

    /// <summary>Forge/NeoForge installer падает без этого файла.</summary>
    private void EnsureLauncherProfiles()
    {
        var path = Path.Combine(InstallRoot, "launcher_profiles.json");
        if (File.Exists(path)) return;

        Directory.CreateDirectory(InstallRoot);
        File.WriteAllText(path, """
            {
              "profiles": {},
              "settings": {},
              "version": 3
            }
            """);
    }
}
