using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MCLauncher.Services;

/// <summary>Найденный в локальной сети открытый мир.</summary>
public sealed class LanWorld
{
    public required string Address { get; init; }
    public required int Port { get; init; }
    public required string Motd { get; init; }
    public DateTime SeenAt { get; init; } = DateTime.Now;

    public string Display => $"{Motd}  —  {Address}:{Port}";
    public override string ToString() => Display;
}

/// <summary>
/// Поиск миров, открытых через «Открыть для сети».
///
/// Minecraft рассылает UDP-мультикаст на 224.0.2.60:4445 каждые 1.5 секунды
/// со строкой вида [MOTD]Мир Стива[/MOTD][AD]58291[/AD], где AD — номер порта.
/// Это избавляет пользователя от ручного ввода порта.
/// </summary>
public sealed class LanDiscoveryService : IDisposable
{
    private const string MulticastGroup = "224.0.2.60";
    private const int MulticastPort = 4445;

    private static readonly Regex MotdRegex = new(@"\[MOTD\](?<motd>.*?)\[/MOTD\]",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex PortRegex = new(@"\[AD\](?<port>\d{1,5})\[/AD\]",
        RegexOptions.Compiled);

    private UdpClient? _client;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<string, LanWorld> _found = new();

    public event Action<LanWorld>? WorldFound;
    public event Action<string>? Status;

    public bool IsScanning => _cts is not null && !_cts.IsCancellationRequested;

    public IReadOnlyCollection<LanWorld> Found
    {
        get { lock (_found) return _found.Values.ToList(); }
    }

    /// <summary>Начинает слушать мультикаст. Работает, пока не вызовут Stop.</summary>
    public void Start()
    {
        if (IsScanning) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            _client = new UdpClient();
            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _client.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));

            // Подписываемся на группу на всех сетевых интерфейсах —
            // иначе на машинах с VPN или несколькими адаптерами пакеты теряются
            var joined = 0;
            foreach (var ip in GetLocalAddresses())
            {
                try
                {
                    _client.JoinMulticastGroup(IPAddress.Parse(MulticastGroup), ip);
                    joined++;
                }
                catch { /* интерфейс не поддерживает мультикаст */ }
            }

            if (joined == 0)
                _client.JoinMulticastGroup(IPAddress.Parse(MulticastGroup));

            Status?.Invoke($"Слушаю локальную сеть (интерфейсов: {Math.Max(joined, 1)})...");
            _ = Task.Run(() => ListenAsync(ct), ct);
        }
        catch (Exception ex)
        {
            Status?.Invoke("Не удалось начать поиск: " + ex.Message);
            Log.Warn("LAN discovery: " + ex.Message);
            Stop();
        }
    }

    private static List<IPAddress> GetLocalAddresses()
    {
        var list = new List<IPAddress>();

        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (!ni.SupportsMulticast) continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        list.Add(addr.Address);
                }
            }
        }
        catch { }

        return list;
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _client is not null)
        {
            try
            {
                var result = await _client.ReceiveAsync(ct).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(result.Buffer);

                var portMatch = PortRegex.Match(text);
                if (!portMatch.Success) continue;
                if (!int.TryParse(portMatch.Groups["port"].Value, out var port)) continue;
                if (port is < 1 or > 65535) continue;

                var motd = MotdRegex.Match(text) is { Success: true } m
                    ? StripColors(m.Groups["motd"].Value)
                    : "Мир Minecraft";

                var host = result.RemoteEndPoint.Address.ToString();
                var key = $"{host}:{port}";

                LanWorld world;
                bool isNew;

                lock (_found)
                {
                    isNew = !_found.ContainsKey(key);
                    world = new LanWorld { Address = host, Port = port, Motd = motd.Trim() };
                    _found[key] = world;
                }

                if (isNew)
                {
                    Log.Info($"Найден LAN-мир: {motd} ({key})");
                    WorldFound?.Invoke(world);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Log.Warn("LAN listen: " + ex.Message);
                await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static string StripColors(string s)
    {
        if (!s.Contains('§')) return s;

        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '§' && i + 1 < s.Length) { i++; continue; }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>Разовый поиск с ожиданием. Minecraft шлёт пакеты каждые 1.5 с.</summary>
    public async Task<List<LanWorld>> ScanOnceAsync(int timeoutMs = 5000, CancellationToken ct = default)
    {
        lock (_found) _found.Clear();

        Start();

        try
        {
            await Task.Delay(timeoutMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        var result = Found.ToList();
        Stop();

        return result;
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }

        try
        {
            _client?.Close();
            _client?.Dispose();
        }
        catch { }

        _client = null;
        _cts = null;
    }

    public void Dispose() => Stop();
}
