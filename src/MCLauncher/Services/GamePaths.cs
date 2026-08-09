using MCLauncher.Models;

namespace MCLauncher.Services;

/// <summary>
/// Набор путей для конкретного запуска.
///
/// Обычная сборка использует общее хранилище (%APPDATA%\.mayslauncher\libraries и т.д.) —
/// это экономит гигабайты, потому что ассеты и библиотеки переиспользуются.
///
/// Изолированная сборка держит всё внутри своей папки: её можно скопировать
/// на флешку, удалить или сломать, не задев остальные.
/// </summary>
public sealed class GamePaths
{
    public required string VersionsDir { get; init; }
    public required string LibrariesDir { get; init; }
    public required string AssetsDir { get; init; }
    public required string NativesRoot { get; init; }
    public required string LogConfigsDir { get; init; }
    public required bool IsIsolated { get; init; }

    public string AssetsIndexesDir => Path.Combine(AssetsDir, "indexes");
    public string AssetsObjectsDir => Path.Combine(AssetsDir, "objects");
    public string AssetsVirtualDir => Path.Combine(AssetsDir, "virtual");

    public string VersionDir(string id) => Path.Combine(VersionsDir, id);
    public string VersionJson(string id) => Path.Combine(VersionDir(id), id + ".json");
    public string VersionJar(string id) => Path.Combine(VersionDir(id), id + ".jar");
    public string NativesDir(string id) => Path.Combine(NativesRoot, id);

    /// <summary>Общее хранилище лаунчера.</summary>
    public static GamePaths Shared => new()
    {
        VersionsDir = LauncherPaths.VersionsDir,
        LibrariesDir = LauncherPaths.LibrariesDir,
        AssetsDir = LauncherPaths.AssetsDir,
        NativesRoot = LauncherPaths.NativesRoot,
        LogConfigsDir = LauncherPaths.LogConfigsDir,
        IsIsolated = false
    };

    /// <summary>Пути внутри папки сборки.</summary>
    public static GamePaths ForInstance(GameInstance inst)
    {
        if (!inst.Isolated) return Shared;

        var root = Path.Combine(InstanceService.InstanceDir(inst), ".minecraft");

        return new GamePaths
        {
            VersionsDir = Path.Combine(root, "versions"),
            LibrariesDir = Path.Combine(root, "libraries"),
            AssetsDir = Path.Combine(root, "assets"),
            NativesRoot = Path.Combine(root, "natives"),
            LogConfigsDir = Path.Combine(root, "log_configs"),
            IsIsolated = true
        };
    }

    public void EnsureAll()
    {
        foreach (var d in new[]
                 {
                     VersionsDir, LibrariesDir, AssetsDir, AssetsIndexesDir,
                     AssetsObjectsDir, NativesRoot, LogConfigsDir
                 })
        {
            Directory.CreateDirectory(d);
        }
    }

    /// <summary>Размер хранилища сборки (для отображения в настройках).</summary>
    public long CalculateSize()
    {
        long total = 0;
        foreach (var dir in new[] { VersionsDir, LibrariesDir, AssetsDir, NativesRoot })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                total += new DirectoryInfo(dir)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length);
            }
            catch { }
        }
        return total;
    }
}
