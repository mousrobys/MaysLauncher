using Open.Nat;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MCLauncher.Services;

public class UpnpService : IDisposable
{
    private NatDevice? _device;
    private Mapping? _mapping;
    private bool _disposed;

    public event Action<string>? OnStatus;
    public bool IsPortOpen { get; private set; }

    public async Task<bool> OpenPortAsync(int port, string description = "MaysLauncher Server")
    {
        try
        {
            OnStatus?.Invoke("Поиск UPnP устройства...");

            var discoverer = new NatDiscoverer();
            _device = await discoverer.DiscoverDeviceAsync();

            OnStatus?.Invoke($"UPnP устройство найдено: {_device.GetExternalIPAsync().Result}");

            _mapping = new Mapping(Protocol.Tcp, port, port, description);
            await _device.CreatePortMapAsync(_mapping);

            IsPortOpen = true;
            OnStatus?.Invoke($"Порт {port} успешно открыт через UPnP");
            return true;
        }
        catch (Exception ex)
        {
            IsPortOpen = false;
            OnStatus?.Invoke($"Не удалось открыть порт: {ex.Message}");
            return false;
        }
    }

    public async Task ClosePortAsync()
    {
        try
        {
            if (_device != null && _mapping != null)
            {
                await _device.DeletePortMapAsync(_mapping);
                OnStatus?.Invoke($"Порт {_mapping.PrivatePort} закрыт");
            }
        }
        catch (Exception ex)
        {
            OnStatus?.Invoke($"Ошибка закрытия порта: {ex.Message}");
        }
        finally
        {
            IsPortOpen = false;
            _mapping = null;
            _device = null;
        }
    }

    public string? GetExternalIp()
    {
        try
        {
            return _device?.GetExternalIPAsync().Result.ToString();
        }
        catch
        {
            return null;
        }
    }

    public string? GetLocalIp()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                    return ip.ToString();
            }
        }
        catch { }
        return null;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            ClosePortAsync().Wait();
            _disposed = true;
        }
    }
}
