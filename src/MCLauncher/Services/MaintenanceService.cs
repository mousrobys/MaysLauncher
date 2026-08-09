using System.Diagnostics;
using System.Text;

namespace MCLauncher.Services;

/// <summary>
/// Обслуживание лаунчера: выборочная очистка, полный сброс и удаление.
/// Все операции необратимы, поэтому каждая описывает, что именно удалит.
/// </summary>
public static class MaintenanceService
{
    /// <summary>Что можно удалить.</summary>
    public enum CleanTarget
    {
        Cache,
        ImageCache,
        Logs,
        Versions,
        Libraries,
        Assets,
        JavaRuntime,
        Instances,
        Settings,
        Account
    }

    public sealed class TargetInfo
    {
        public required CleanTarget Target { get; init; }
        public required string Title { get; init; }
        public required string Description { get; init; }
        public required string Path { get; init; }
        public long Size { get; set; }
        public bool IsFile { get; init; }
        public bool Dangerous { get; init; }

        public string SizeDisplay
        {
            get
            {
                string[] u = { "Б", "КБ", "МБ", "ГБ" };
                double v = Size;
                var i = 0;
                while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
                return $"{v:0.#} {u[i]}";
            }
        }
    }

    public static List<TargetInfo> Enumerate()
    {
        var list = new List<TargetInfo>
        {
            new()
            {
                Target = CleanTarget.Cache, Title = "Временные файлы",
                Description = "Установщики загрузчиков, кэш манифеста. Безопасно.",
                Path = LauncherPaths.CacheDir
            },
            new()
            {
                Target = CleanTarget.ImageCache, Title = "Кэш изображений",
                Description = "Иконки модов и серверов. Скачаются заново.",
                Path = System.IO.Path.Combine(LauncherPaths.CacheDir, "images")
            },
            new()
            {
                Target = CleanTarget.Logs, Title = "Журнал лаунчера",
                Description = "История событий. На работу не влияет.",
                Path = LauncherPaths.LauncherLogFile, IsFile = true
            },
            new()
            {
                Target = CleanTarget.JavaRuntime, Title = "Скачанная Java",
                Description = "Портативные JRE. Будут загружены заново при запуске.",
                Path = LauncherPaths.RuntimeDir
            },
            new()
            {
                Target = CleanTarget.Assets, Title = "Ресурсы игры",
                Description = "Звуки, языки, текстуры. Это самая объёмная папка.",
                Path = LauncherPaths.AssetsDir
            },
            new()
            {
                Target = CleanTarget.Libraries, Title = "Библиотеки",
                Description = "Общие JAR-файлы для всех версий.",
                Path = LauncherPaths.LibrariesDir
            },
            new()
            {
                Target = CleanTarget.Versions, Title = "Версии игры",
                Description = "Клиенты Minecraft и профили загрузчиков.",
                Path = LauncherPaths.VersionsDir
            },
            new()
            {
                Target = CleanTarget.Instances, Title = "Сборки целиком",
                Description = "МОДЫ, МИРЫ, СКРИНШОТЫ И НАСТРОЙКИ. Восстановить нельзя.",
                Path = InstanceService.InstancesRoot, Dangerous = true
            },
            new()
            {
                Target = CleanTarget.Settings, Title = "Настройки лаунчера",
                Description = "Тема, память, пути. Вернутся значения по умолчанию.",
                Path = LauncherPaths.SettingsFile, IsFile = true
            },
            new()
            {
                Target = CleanTarget.Account, Title = "Аккаунт",
                Description = "Сохранённый профиль. Придётся войти заново.",
                Path = LauncherPaths.AccountFile, IsFile = true
            }
        };

        foreach (var t in list) t.Size = Measure(t.Path, t.IsFile);
        return list;
    }

    private static long Measure(string path, bool isFile)
    {
        try
        {
            if (isFile) return File.Exists(path) ? new FileInfo(path).Length : 0;

            if (!Directory.Exists(path)) return 0;
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch { return 0; }
    }

    /// <summary>Удаляет выбранные категории. Возвращает освобождённые байты.</summary>
    public static long Clean(IEnumerable<TargetInfo> targets)
    {
        long freed = 0;

        foreach (var t in targets)
        {
            try
            {
                var size = Measure(t.Path, t.IsFile);

                if (t.IsFile)
                {
                    if (File.Exists(t.Path)) File.Delete(t.Path);

                    // Резервные копии тоже
                    foreach (var extra in new[] { t.Path + ".bak", t.Path + ".tmp" })
                        if (File.Exists(extra)) File.Delete(extra);
                }
                else if (Directory.Exists(t.Path))
                {
                    Directory.Delete(t.Path, true);
                }

                freed += size;
                Log.Info($"Очищено: {t.Title} ({size / 1048576} МБ)");
            }
            catch (Exception ex)
            {
                Log.Warn($"Не удалось очистить «{t.Title}»: {ex.Message}");
            }
        }

        LauncherPaths.EnsureAll();
        return freed;
    }

    /// <summary>Полное удаление данных лаунчера с созданием bat-скрипта для самоудаления exe.</summary>
    public static string PrepareUninstall(bool removeExe)
    {
        var root = LauncherPaths.Root;
        var exe = Environment.ProcessPath ?? "";

        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine("chcp 65001 >nul");
        script.AppendLine("echo Удаление MaysLauncher...");

        // Ждём завершения процесса
        script.AppendLine("timeout /t 2 /nobreak >nul");

        script.AppendLine($"rmdir /s /q \"{root}\" 2>nul");

        if (removeExe && exe.Length > 0 && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            script.AppendLine("timeout /t 1 /nobreak >nul");
            script.AppendLine($"del /f /q \"{exe}\" 2>nul");
        }

        script.AppendLine("echo Готово. Окно закроется автоматически.");
        script.AppendLine("timeout /t 3 /nobreak >nul");
        script.AppendLine("del \"%~f0\" 2>nul");

        var path = Path.Combine(Path.GetTempPath(), "mayslauncher_uninstall.bat");
        File.WriteAllText(path, script.ToString(), Encoding.UTF8);

        return path;
    }

    public static void RunUninstall(string scriptPath)
    {
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"")
        {
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Minimized
        });
    }

    /// <summary>Полный размер данных лаунчера.</summary>
    public static long TotalSize() => Measure(LauncherPaths.Root, false);
}
