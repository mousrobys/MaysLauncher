namespace MCLauncher.Services;

/// <summary>Все пути лаунчера в одном месте. Структура 1-в-1 как у официального лаунчера.</summary>
public static class LauncherPaths
{
    private const string FolderName = "MaysLauncher";
    private const string LegacyFolderName = ".mayslauncher";

    /// <summary>Маркер портативного режима рядом с exe.</summary>
    public const string PortableMarker = "portable.txt";

    /// <summary>Данные лежат рядом с exe (запуск с флешки).</summary>
    public static bool IsPortable { get; private set; }

    /// <summary>Корень данных: %APPDATA%\MaysLauncher либо папка рядом с exe.</summary>
    public static string Root { get; private set; } = ResolveRoot();

    /// <summary>Папка, где лежит сам исполняемый файл.</summary>
    public static string ExeDir
    {
        get
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    var dir = Path.GetDirectoryName(exe);
                    if (!string.IsNullOrEmpty(dir)) return dir;
                }
            }
            catch { }

            return AppContext.BaseDirectory;
        }
    }

    private static string PortableRoot => Path.Combine(ExeDir, "MaysLauncherData");

    /// <summary>
    /// Если рядом с exe есть portable.txt — работаем оттуда.
    /// Так лаунчер можно носить на флешке вместе со сборками.
    /// </summary>
    private static string ResolveRoot()
    {
        try
        {
            if (File.Exists(Path.Combine(ExeDir, PortableMarker)))
            {
                IsPortable = true;
                return PortableRoot;
            }
        }
        catch { }

        IsPortable = false;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var canonical = Path.Combine(appData, FolderName);
        var legacy = Path.Combine(appData, LegacyFolderName);

        // Одноразовый перенос данных из старой папки .mayslauncher в MaysLauncher.
        // Идём только атомарным переименованием: если что-то занято — остаёмся в старой
        // папке до следующего запуска, ничего не портя.
        if (!Directory.Exists(canonical) && Directory.Exists(legacy))
        {
            try
            {
                Directory.Move(legacy, canonical);
            }
            catch { return legacy; }
        }

        return canonical;
    }

    /// <summary>Проверяет, можно ли писать рядом с exe (на CD или в Program Files нельзя).</summary>
    public static bool CanUsePortable()
    {
        try
        {
            var probe = Path.Combine(ExeDir, ".write_test");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Включает или выключает портативный режим. Требует перезапуска.</summary>
    public static void SetPortable(bool enabled)
    {
        var marker = Path.Combine(ExeDir, PortableMarker);

        if (enabled)
        {
            Directory.CreateDirectory(PortableRoot);
            File.WriteAllText(marker,
                "Этот файл включает портативный режим MaysLauncher.\r\n" +
                "Данные хранятся в папке MaysLauncherData рядом с лаунчером.\r\n" +
                "Удалите файл, чтобы вернуться к хранению в %APPDATA%.\r\n");
        }
        else if (File.Exists(marker))
        {
            File.Delete(marker);
        }
    }

    /// <summary>Переносит данные между обычным и портативным расположением.</summary>
    public static void MigrateTo(bool toPortable, Action<string>? progress = null)
    {
        var from = toPortable
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName)
            : PortableRoot;

        var to = toPortable
            ? PortableRoot
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

        if (!Directory.Exists(from) || string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;

        Directory.CreateDirectory(to);
        CopyRecursive(new DirectoryInfo(from), to, progress);
    }

    private static void CopyRecursive(DirectoryInfo src, string dst, Action<string>? progress)
    {
        Directory.CreateDirectory(dst);

        foreach (var file in src.GetFiles())
        {
            try
            {
                file.CopyTo(Path.Combine(dst, file.Name), true);
                progress?.Invoke(file.Name);
            }
            catch { }
        }

        foreach (var dir in src.GetDirectories())
            CopyRecursive(dir, Path.Combine(dst, dir.Name), progress);
    }

    public static string VersionsDir => Path.Combine(Root, "versions");
    public static string LibrariesDir => Path.Combine(Root, "libraries");
    public static string AssetsDir => Path.Combine(Root, "assets");
    public static string AssetsIndexesDir => Path.Combine(AssetsDir, "indexes");
    public static string AssetsObjectsDir => Path.Combine(AssetsDir, "objects");
    public static string AssetsVirtualDir => Path.Combine(AssetsDir, "virtual");
    public static string NativesRoot => Path.Combine(Root, "natives");
    public static string RuntimeDir => Path.Combine(Root, "runtime");
    public static string LogConfigsDir => Path.Combine(Root, "log_configs");
    public static string CacheDir => Path.Combine(Root, "cache");

    public static string AccountFile => Path.Combine(Root, "account.dat");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LauncherLogFile => Path.Combine(Root, "launcher.log");

    public static string VersionDir(string versionId) => Path.Combine(VersionsDir, versionId);
    public static string VersionJson(string versionId) => Path.Combine(VersionDir(versionId), versionId + ".json");
    public static string VersionJar(string versionId) => Path.Combine(VersionDir(versionId), versionId + ".jar");
    public static string NativesDir(string versionId) => Path.Combine(NativesRoot, versionId);

    public static void SetRoot(string root)
    {
        if (!string.IsNullOrWhiteSpace(root)) Root = root;
    }

    public static void EnsureAll()
    {
        foreach (var d in new[]
                 {
                     Root, VersionsDir, LibrariesDir, AssetsDir, AssetsIndexesDir,
                     AssetsObjectsDir, NativesRoot, RuntimeDir, LogConfigsDir, CacheDir
                 })
        {
            Directory.CreateDirectory(d);
        }
    }
}
