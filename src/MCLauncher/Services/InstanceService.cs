using System.Diagnostics;
using System.Text.Json;
using MCLauncher.Models;

namespace MCLauncher.Services;

/// <summary>
/// Управление сборками: каждая имеет изолированную папку с модами,
/// ресурспаками, шейдерами, мирами и скриншотами (подход PrismLauncher).
/// </summary>
public static class InstanceService
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string InstancesRoot => Path.Combine(LauncherPaths.Root, "instances");

    public static string InstanceDir(GameInstance inst) => Path.Combine(InstancesRoot, inst.Id);
    public static string ModsDir(GameInstance inst) => Path.Combine(InstanceDir(inst), "mods");
    public static string ResourcePacksDir(GameInstance inst) => Path.Combine(InstanceDir(inst), "resourcepacks");
    public static string ShaderPacksDir(GameInstance inst) => Path.Combine(InstanceDir(inst), "shaderpacks");
    public static string ScreenshotsDir(GameInstance inst) => Path.Combine(InstanceDir(inst), "screenshots");
    public static string SavesDir(GameInstance inst) => Path.Combine(InstanceDir(inst), "saves");
    public static string LogsDir(GameInstance inst) => Path.Combine(InstanceDir(inst), "logs");
    public static string CrashReportsDir(GameInstance inst) => Path.Combine(InstanceDir(inst), "crash-reports");
    public static string ConfigDir(GameInstance inst) => Path.Combine(InstanceDir(inst), "config");

    private static string IndexFile => Path.Combine(InstancesRoot, "instances.json");

    // ---------------- CRUD ----------------

    private static string BackupFile => IndexFile + ".bak";

    /// <summary>
    /// Признак того, что список успешно прочитан с диска.
    /// Пока он false, сохранять нельзя — иначе пустой список затрёт реальные сборки.
    /// </summary>
    public static bool Loaded { get; private set; }

    public static List<GameInstance> LoadAll()
    {
        // Сначала основной файл, затем резервная копия
        foreach (var file in new[] { IndexFile, BackupFile })
        {
            try
            {
                if (!File.Exists(file)) continue;

                var json = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(json)) continue;

                var list = JsonSerializer.Deserialize<List<GameInstance>>(json);
                if (list is null) continue;

                if (file == BackupFile)
                    Log.Warn("Основной список сборок повреждён — восстановлен из резервной копии.");

                Loaded = true;
                return list;
            }
            catch (Exception ex)
            {
                Log.Warn($"Не удалось прочитать {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        // Файлов нет вовсе — это чистая установка, сохранять безопасно
        if (!File.Exists(IndexFile) && !File.Exists(BackupFile))
        {
            Loaded = true;
            return new List<GameInstance>();
        }

        // Файл есть, но не читается: блокируем запись, чтобы не потерять данные
        Loaded = false;
        Log.Error("Список сборок не удалось прочитать. Сохранение отключено до перезапуска, " +
                  "чтобы не потерять существующие сборки.");
        return new List<GameInstance>();
    }

    /// <summary>Атомарное сохранение: temp -> replace, с резервной копией.</summary>
    public static void SaveAll(IEnumerable<GameInstance> instances)
    {
        if (!Loaded)
        {
            Log.Warn("Сохранение сборок пропущено: список не был прочитан корректно.");
            return;
        }

        try
        {
            Directory.CreateDirectory(InstancesRoot);

            var list = instances.ToList();
            var json = JsonSerializer.Serialize(list, Opts);

            // Пустой список поверх непустого файла — почти наверняка ошибка
            if (list.Count == 0 && File.Exists(IndexFile) && new FileInfo(IndexFile).Length > 8)
            {
                Log.Warn("Попытка сохранить пустой список поверх существующего — отменено.");
                return;
            }

            var tmp = IndexFile + ".tmp";
            File.WriteAllText(tmp, json);

            if (File.Exists(IndexFile))
            {
                try { File.Copy(IndexFile, BackupFile, true); } catch { }
                File.Replace(tmp, IndexFile, null);
            }
            else
            {
                File.Move(tmp, IndexFile);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось сохранить список сборок: " + ex.Message);
        }
    }

    /// <summary>
    /// Восстанавливает сборки по папкам на диске, если индекс потерялся.
    /// Возвращает найденные, но отсутствующие в списке сборки.
    /// </summary>
    public static List<GameInstance> ScanOrphans(List<GameInstance> known)
    {
        var found = new List<GameInstance>();

        try
        {
            if (!Directory.Exists(InstancesRoot)) return found;

            var knownIds = known.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in Directory.GetDirectories(InstancesRoot))
            {
                var id = Path.GetFileName(dir);
                if (knownIds.Contains(id)) continue;

                // Папка сборки узнаётся по характерному содержимому
                var looksLikeInstance =
                    Directory.Exists(Path.Combine(dir, "mods")) ||
                    Directory.Exists(Path.Combine(dir, "saves")) ||
                    Directory.Exists(Path.Combine(dir, ".minecraft")) ||
                    File.Exists(Path.Combine(dir, "options.txt"));

                if (!looksLikeInstance) continue;

                // Пытаемся определить версию по изолированному хранилищу
                var mcVersion = "";
                var isolated = Directory.Exists(Path.Combine(dir, ".minecraft"));
                var versionsDir = Path.Combine(dir, ".minecraft", "versions");

                if (Directory.Exists(versionsDir))
                {
                    var vers = Directory.GetDirectories(versionsDir).Select(Path.GetFileName).ToList();
                    mcVersion = vers.FirstOrDefault(v => v is not null &&
                                    VersionService.ParseMcVersion(v) is not null) ?? "";
                }

                if (string.IsNullOrEmpty(mcVersion))
                    mcVersion = GameOptionsService.GetLanguage(dir) is not null ? "1.20.1" : "1.20.1";

                found.Add(new GameInstance
                {
                    Id = id,
                    Name = $"Minecraft {mcVersion}",
                    McVersion = mcVersion,
                    Loader = LoaderKind.Vanilla,
                    LaunchVersionId = mcVersion,
                    Isolated = isolated,
                    IconColor = "#38BDF8"
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось просканировать папки сборок: " + ex.Message);
        }

        return found;
    }

    /// <summary>Создаёт папки сборки.</summary>
    public static void EnsureFolders(GameInstance inst)
    {
        foreach (var d in new[]
                 {
                     InstanceDir(inst), ModsDir(inst), ResourcePacksDir(inst), ShaderPacksDir(inst),
                     ScreenshotsDir(inst), SavesDir(inst), LogsDir(inst), ConfigDir(inst)
                 })
        {
            Directory.CreateDirectory(d);
        }
    }

    public static void Delete(GameInstance inst, bool deleteFiles)
    {
        if (!deleteFiles) return;

        try
        {
            if (Directory.Exists(InstanceDir(inst)))
                Directory.Delete(InstanceDir(inst), true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось удалить папку сборки: {ex.Message}");
            throw new IOException(
                "Не удалось удалить папку сборки. Возможно, файлы заняты другой программой.", ex);
        }
    }

    // ---------------- Содержимое ----------------

    public sealed class FolderStats
    {
        public int Mods { get; init; }
        public int ResourcePacks { get; init; }
        public int ShaderPacks { get; init; }
        public int Screenshots { get; init; }
        public int Worlds { get; init; }
        public long TotalBytes { get; init; }

        public string SizeDisplay
        {
            get
            {
                string[] u = { "Б", "КБ", "МБ", "ГБ" };
                double v = TotalBytes;
                var i = 0;
                while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
                return $"{v:0.#} {u[i]}";
            }
        }
    }

    public static FolderStats GetStats(GameInstance inst)
    {
        int Count(string dir, params string[] patterns)
        {
            if (!Directory.Exists(dir)) return 0;
            try
            {
                return patterns.Length == 0
                    ? Directory.GetFiles(dir).Length
                    : patterns.Sum(p => Directory.GetFiles(dir, p).Length);
            }
            catch { return 0; }
        }

        long size = 0;
        try
        {
            var d = InstanceDir(inst);
            if (Directory.Exists(d))
                size = new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch { }

        var worlds = 0;
        try
        {
            if (Directory.Exists(SavesDir(inst)))
                worlds = Directory.GetDirectories(SavesDir(inst)).Length;
        }
        catch { }

        return new FolderStats
        {
            Mods = Count(ModsDir(inst), "*.jar", "*.disabled"),
            ResourcePacks = Count(ResourcePacksDir(inst), "*.zip"),
            ShaderPacks = Count(ShaderPacksDir(inst), "*.zip"),
            Screenshots = Count(ScreenshotsDir(inst), "*.png"),
            Worlds = worlds,
            TotalBytes = size
        };
    }

    /// <summary>Скриншоты, отсортированные от новых к старым.</summary>
    public static List<FileInfo> GetScreenshots(GameInstance inst, int limit = 200)
    {
        var dir = ScreenshotsDir(inst);
        if (!Directory.Exists(dir)) return new List<FileInfo>();

        try
        {
            return new DirectoryInfo(dir)
                .GetFiles("*.png")
                .OrderByDescending(f => f.LastWriteTime)
                .Take(limit)
                .ToList();
        }
        catch { return new List<FileInfo>(); }
    }

    /// <summary>Открывает папку в проводнике, создавая её при необходимости.</summary>
    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось открыть папку {path}: {ex.Message}");
            throw new IOException("Не удалось открыть папку: " + ex.Message, ex);
        }
    }

    /// <summary>Открывает проводник с выделенным файлом.</summary>
    public static void RevealFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) { OpenFolder(Path.GetDirectoryName(filePath)!); return; }
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось показать файл: {ex.Message}");
        }
    }
}
