using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace MCLauncher.Services;

/// <summary>
/// Самообновление MaysLauncher (Self-Replacement).
/// Постоянное место лаунчера — Рабочий стол, имя файла — MaysLauncher.exe.
///
/// Правила запуска:
///  - Из ЛЮБОГО места (Рабочий стол, «билды», флешка, любая папка) и у ЛЮБОГО
///    пользователя Windows лаунчер просто открывается — все пути (данные в
///    %APPDATA%\MaysLauncher, Рабочий стол, «Загрузки») определяются автоматически
///    под текущую учётную запись.
///  - Режим обновлятора включается ТОЛЬКО когда файл запущен из папки «Загрузки»
///    текущего пользователя (так обычно выглядит скачанная с GitHub новая версия).
///    Тогда он: а) принудительно закрывает старый процесс лаунчера;
///    б) копирует себя на Рабочий стол ПОВЕРХ старого MaysLauncher.exe;
///    в) запускает обновлённый лаунчер с Рабочего стола;
///    г) завершает себя (временный файл из «Загрузок» удаляется автоматически).
///  - Версию на рабочем столе НИКОГДА не понижаем: если установленная копия не
///    старее запущенной — просто открываемся как обычно.
/// Папка данных (игры, сборки, моды, аккаунты, настройки) при обновлении не трогается.
/// </summary>
public static class SelfUpdateService
{
    private const string ProcessName = "MaysLauncher";

    public const string ExeName = "MaysLauncher.exe";

    /// <summary>Постоянное место лаунчера — Рабочий стол текущего пользователя.</summary>
    public static string DesktopExePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), ExeName);

    private static string CurrentExePath => Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, ExeName);

    /// <summary>
    /// Вызывается в самом начале запуска (до построения окна).
    /// Возвращает true, если текущий процесс — обновлятор, который уже сделал замену
    /// и теперь приложению нужно завершиться.
    /// </summary>
    public static bool RunSelfReplacementIfNeeded()
    {
        try
        {
            var current = Path.GetFullPath(CurrentExePath);
            var desktop = Path.GetFullPath(DesktopExePath);

            // Уже каноническая копия с Рабочего стола — запускаемся как обычно.
            if (string.Equals(current, desktop, StringComparison.OrdinalIgnoreCase))
                return false;

            // Замена выполняется только для файла, запущенного из «Загрузок».
            // С любого другого места лаунчер просто открывается.
            if (!IsUnderDownloads(Path.GetDirectoryName(current)!))
                return false;

            // Не понижаем версию: если на Рабочем столе копия не старее нас —
            // обновлять нечего, открываемся как есть.
            if (File.Exists(desktop) && GetVersion(desktop) >= GetVersion(current))
                return false;

            // 1) Принудительно закрываем старые процессы лаунчера
            KillOldInstances();

            // 2) Копируем себя на Рабочий стол ПОВЕРХ старого файла
            var desktopDir = Path.GetDirectoryName(desktop);
            if (string.IsNullOrEmpty(desktopDir)) return false;
            Directory.CreateDirectory(desktopDir);

            var copied = false;
            for (var i = 0; i < 25 && !copied; i++)
            {
                try
                {
                    File.Copy(current, desktop, overwrite: true);
                    copied = true;
                }
                catch (IOException) { Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { Thread.Sleep(300); }
            }

            if (!copied) return false;

            // 3) Запускаем обновлённый лаунчер с Рабочего стола
            Process.Start(new ProcessStartInfo
            {
                FileName = desktop,
                WorkingDirectory = desktopDir,
                UseShellExecute = true
            });

            // 4) Планируем удаление временного файла (себя) и завершаемся
            ScheduleSelfDelete(current);
            return true;
        }
        catch
        {
            // Ничего страшного: если замена не удалась — просто запускаем UI как есть
            return false;
        }
    }

    private static void KillOldInstances()
    {
        var self = Process.GetCurrentProcess().Id;
        foreach (var p in Process.GetProcessesByName(ProcessName))
        {
            if (p.Id == self) continue;
            try
            {
                p.Kill();
                p.WaitForExit(8000);
            }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }
    }

    /// <summary>Запускает фоновый cmd, который удалит временный exe после выхода процесса.</summary>
    private static void ScheduleSelfDelete(string path)
    {
        try
        {
            var script = Path.Combine(Path.GetTempPath(), "mays_del_" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(script,
                "@echo off\r\n" +
                "del /f /q \"" + path + "\" >nul 2>&1\r\n" +
                "del /f /q \"" + script + "\" >nul 2>&1\r\n");

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c start \"\" /min \"" + script + "\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch { }
    }

    /// <summary>Версия exe из его метаданных (не читая сам файл целиком).</summary>
    private static Version GetVersion(string path)
    {
        try
        {
            return AssemblyName.GetAssemblyName(path).Version ?? new Version(0, 0, 0, 0);
        }
        catch
        {
            return new Version(0, 0, 0, 0);
        }
    }

    /// <summary>Папка «Загрузки» текущего пользователя (с учётом OneDrive и русской Windows).</summary>
    private static string GetDownloadsDir()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders");
            var value = key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") as string;
            if (!string.IsNullOrEmpty(value)) return value;
        }
        catch { }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var en = Path.Combine(profile, "Downloads");
        if (Directory.Exists(en)) return en;
        return Path.Combine(profile, "Загрузки");
    }

    private static bool IsUnderDownloads(string dir)
    {
        try
        {
            var downloads = new DirectoryInfo(GetDownloadsDir());
            if (!downloads.Exists) return false;

            var current = new DirectoryInfo(dir ?? string.Empty);
            while (current != null)
            {
                if (string.Equals(current.FullName.TrimEnd('\\'),
                        downloads.FullName.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return true;
                current = current.Parent;
            }
        }
        catch { }
        return false;
    }
}