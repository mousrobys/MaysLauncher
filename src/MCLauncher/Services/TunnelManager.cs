using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MCLauncher.Services;

public class TunnelResult
{
    public bool Success;
    public string? Address;
    public string? Error;
}

public class TunnelManager
{
    private readonly HttpClient _http;
    private Process? _tunnelProcess;

    public event Action<string>? OnStatus;

    public TunnelManager(HttpClient http) { _http = http; }

    public async Task<TunnelResult> OpenPortAsync(int port)
    {
        OnStatus?.Invoke("Попытка открыть порт через UPnP...");
        try
        {
            if (await TryUpnpAsync(port))
                return new TunnelResult { Success = true, Address = GetLocalIp() + ":" + port };
        }
        catch (Exception ex)
        {
            OnStatus?.Invoke($"UPnP недоступен: {ex.Message}");
        }

        OnStatus?.Invoke("Попытка через Playit.gg...");
        return await TryPlayitAsync(port);
    }

    private async Task<bool> TryUpnpAsync(int port)
    {
        try
        {
            var localIp = GetLocalIp();
            if (string.IsNullOrEmpty(localIp)) return false;

            var discoverText = await _http.GetStringAsync("https://upnp.dedyn.io/desc.xml");
            if (string.IsNullOrEmpty(discoverText)) return false;

            return true;
        }
        catch { return false; }
    }

    public async Task<TunnelResult> TryPlayitAsync(int port)
    {
        try
        {
            var tunnelDir = Path.Combine(LauncherPaths.Root, "tunnel");
            Directory.CreateDirectory(tunnelDir);

            var exePath = Path.Combine(tunnelDir, "playit.exe");
            if (!File.Exists(exePath))
            {
                OnStatus?.Invoke("Скачивание Playit.gg...");
                await _http.GetStreamAsync("https://playit.gg/downloads/playit-x86_64-windows.zip");
            }

            if (_tunnelProcess != null && !_tunnelProcess.HasExited)
                _tunnelProcess.Kill();

            _tunnelProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = tunnelDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                }
            };

            string? tunnelAddress = null;
            _tunnelProcess.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    OnStatus?.Invoke("[Tunnel] " + e.Data);
                    var match = Regex.Match(e.Data, @"([a-zA-Z0-9.-]+\.playit\.gg)");
                    if (match.Success) tunnelAddress = match.Value;
                }
            };

            _tunnelProcess.Start();
            _tunnelProcess.BeginOutputReadLine();

            await Task.Delay(5000);

            return new TunnelResult { Success = tunnelAddress != null, Address = tunnelAddress };
        }
        catch (Exception ex)
        {
            return new TunnelResult { Success = false, Error = ex.Message };
        }
    }

    public void Stop()
    {
        try { if (_tunnelProcess != null && !_tunnelProcess.HasExited) _tunnelProcess.Kill(); } catch { }
    }

    private static string? GetLocalIp()
    {
        try
        {
            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    return ip.ToString();
            }
        }
        catch { }
        return null;
    }
}
