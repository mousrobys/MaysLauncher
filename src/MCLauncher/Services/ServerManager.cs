using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;

namespace MCLauncher.Services;

public class ServerManager
{
    private Process? _process;
    private StreamWriter? _inputWriter;
    private readonly Dispatcher _dispatcher;

    public bool IsRunning => _process?.HasExited == false;

    public event Action<string>? OnOutput;
    public event Action<string>? OnError;
    public event Action? OnExited;

    public ServerManager(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public async Task<bool> StartAsync(ServerConfig config)
    {
        if (IsRunning) return true;

        try
        {
            config.EnsureServerDir();

            var jarPath = ServerConfig.GetCoreJar();
            if (!File.Exists(jarPath))
            {
                OnError?.Invoke("Серверный JAR не найден. Сначала скачайте ядро.");
                return false;
            }

            var javaPath = FindJavaPath(config);
            if (string.IsNullOrEmpty(javaPath))
            {
                OnError?.Invoke("Java не найдена. Установите Java или укажите путь в настройках.");
                return false;
            }

            config.WriteEula();
            config.WriteProperties();

            var psi = new ProcessStartInfo(javaPath)
            {
                WorkingDirectory = ServerConfig.GetServerDir(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            psi.ArgumentList.Add($"-Xmx{config.MaxMemoryMb}M");
            psi.ArgumentList.Add($"-Xms{config.MinMemoryMb}M");
            psi.ArgumentList.Add("-jar");
            psi.ArgumentList.Add(jarPath);
            psi.ArgumentList.Add("nogui");

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) _dispatcher.Invoke(() => OnOutput?.Invoke(e.Data));
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) _dispatcher.Invoke(() => OnError?.Invoke(e.Data));
            };
            _process.Exited += (_, _) => _dispatcher.Invoke(() => OnExited?.Invoke());

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _inputWriter = _process.StandardInput;

            return true;
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Ошибка запуска сервера: {ex.Message}");
            return false;
        }
    }

    private static string? FindJavaPath(ServerConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.CustomJavaPath) && File.Exists(config.CustomJavaPath))
            return config.CustomJavaPath;

        if (Directory.Exists(LauncherPaths.RuntimeDir))
        {
            foreach (var dir in Directory.GetDirectories(LauncherPaths.RuntimeDir))
            {
                var javaExe = Path.Combine(dir, "bin", "java.exe");
                if (File.Exists(javaExe)) return javaExe;
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    javaExe = Path.Combine(sub, "bin", "java.exe");
                    if (File.Exists(javaExe)) return javaExe;
                }
            }
        }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var javaExe = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(javaExe)) return javaExe;
        }

        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var javaExe = Path.Combine(p, "java.exe");
            if (File.Exists(javaExe)) return javaExe;
        }

        return null;
    }

    public void SendCommand(string command)
    {
        if (!IsRunning || _inputWriter == null) return;
        try
        {
            _inputWriter.WriteLine(command);
            _inputWriter.Flush();
        }
        catch { }
    }

    public async Task StopAsync(int timeoutMs = 30000)
    {
        if (!IsRunning) return;

        try
        {
            SendCommand("stop");
            var task = Task.Run(() => _process?.WaitForExit(timeoutMs) ?? false);
            if (await task == false)
            {
                try { _process?.Kill(true); } catch { }
            }
        }
        catch { }
        finally
        {
            _inputWriter?.Dispose();
            _inputWriter = null;
            _process?.Dispose();
            _process = null;
        }
    }

    public void Kill()
    {
        try { _process?.Kill(true); } catch { }
        _inputWriter?.Dispose();
        _process?.Dispose();
        _process = null;
        _inputWriter = null;
    }
}
