using MCLauncher.Models;

namespace MCLauncher.Services;

/// <summary>Установленная версия игры в хранилище.</summary>
public sealed class InstalledVersion
{
    public required string Id { get; init; }
    public required string Directory { get; init; }
    public long SizeBytes { get; init; }
    public DateTime Installed { get; init; }
    public bool HasJar { get; init; }
    public string? InheritsFrom { get; init; }
    public bool IsIsolated { get; init; }
    public string? OwnerInstance { get; init; }

    /// <summary>Сборки, которые используют эту версию.</summary>
    public List<string> UsedBy { get; init; } = new();

    public bool InUse => UsedBy.Count > 0;

    public string SizeDisplay
    {
        get
        {
            string[] u = { "Б", "КБ", "МБ", "ГБ" };
            double v = SizeBytes;
            var i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return $"{v:0.#} {u[i]}";
        }
    }

    public string Kind
    {
        get
        {
            var lower = Id.ToLowerInvariant();
            if (lower.Contains("fabric")) return "Fabric";
            if (lower.Contains("neoforge")) return "NeoForge";
            if (lower.Contains("forge")) return "Forge";
            return "Vanilla";
        }
    }
}

/// <summary>Просмотр и удаление установленных версий игры.</summary>
public static class VersionManagerService
{
    /// <summary>Собирает все версии: из общего хранилища и из изолированных сборок.</summary>
    public static List<InstalledVersion> Scan(List<GameInstance> instances)
    {
        var result = new List<InstalledVersion>();

        // Общее хранилище
        result.AddRange(ScanDir(LauncherPaths.VersionsDir, instances, isolated: false, owner: null));

        // Изолированные сборки
        foreach (var inst in instances.Where(i => i.Isolated))
        {
            var paths = GamePaths.ForInstance(inst);
            result.AddRange(ScanDir(paths.VersionsDir, instances, isolated: true, owner: inst.Name));
        }

        return result.OrderByDescending(v => v.SizeBytes).ToList();
    }

    private static List<InstalledVersion> ScanDir(
        string versionsDir, List<GameInstance> instances, bool isolated, string? owner)
    {
        var list = new List<InstalledVersion>();

        try
        {
            if (!Directory.Exists(versionsDir)) return list;

            foreach (var dir in Directory.GetDirectories(versionsDir))
            {
                var id = Path.GetFileName(dir);
                var json = Path.Combine(dir, id + ".json");
                if (!File.Exists(json)) continue;

                long size = 0;
                try
                {
                    size = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)
                        .Sum(f => f.Length);
                }
                catch { }

                string? inherits = null;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
                    if (doc.RootElement.TryGetProperty("inheritsFrom", out var inh))
                        inherits = inh.GetString();
                }
                catch { }

                // Кто использует эту версию
                var usedBy = instances
                    .Where(i =>
                    {
                        if (isolated && !string.Equals(i.Name, owner, StringComparison.Ordinal)) return false;
                        if (!isolated && i.Isolated) return false;

                        return string.Equals(i.McVersion, id, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(i.LaunchVersionId, id, StringComparison.OrdinalIgnoreCase);
                    })
                    .Select(i => i.Name)
                    .ToList();

                list.Add(new InstalledVersion
                {
                    Id = id,
                    Directory = dir,
                    SizeBytes = size,
                    Installed = Directory.GetCreationTime(dir),
                    HasJar = File.Exists(Path.Combine(dir, id + ".jar")),
                    InheritsFrom = inherits,
                    IsIsolated = isolated,
                    OwnerInstance = owner,
                    UsedBy = usedBy
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Сканирование версий в {versionsDir}: {ex.Message}");
        }

        return list;
    }

    /// <summary>Удаляет версию с диска. Возвращает освобождённые байты.</summary>
    public static long Delete(InstalledVersion version)
    {
        var freed = version.SizeBytes;

        if (Directory.Exists(version.Directory))
            Directory.Delete(version.Directory, true);

        // Заодно убираем распакованные natives этой версии
        try
        {
            var natives = Path.Combine(LauncherPaths.NativesRoot, version.Id);
            if (Directory.Exists(natives))
            {
                try { freed += new DirectoryInfo(natives).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
                catch { }
                Directory.Delete(natives, true);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось удалить natives для {version.Id}: {ex.Message}");
        }

        Log.Info($"Версия {version.Id} удалена, освобождено {freed / 1048576} МБ.");
        return freed;
    }

    /// <summary>Библиотеки, на которые больше не ссылается ни одна версия.</summary>
    public static long CleanupUnusedLibraries()
    {
        // Осторожная реализация: удаляем только пустые директории,
        // потому что библиотеки переиспользуются между версиями.
        long freed = 0;

        try
        {
            if (!Directory.Exists(LauncherPaths.LibrariesDir)) return 0;
            RemoveEmptyDirs(LauncherPaths.LibrariesDir, ref freed);
        }
        catch (Exception ex)
        {
            Log.Warn("Очистка библиотек: " + ex.Message);
        }

        return freed;
    }

    private static void RemoveEmptyDirs(string root, ref long freed)
    {
        foreach (var dir in Directory.GetDirectories(root))
        {
            RemoveEmptyDirs(dir, ref freed);

            try
            {
                if (Directory.GetFileSystemEntries(dir).Length == 0)
                {
                    Directory.Delete(dir);
                    freed += 4096;
                }
            }
            catch { }
        }
    }
}
