using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MCLauncher.Models;

namespace MCLauncher.Services;

/// <summary>
/// Реализация протокола Minecraft Server List Ping (handshake + status).
/// Поддерживает SRV-записи (_minecraft._tcp.&lt;домен&gt;), как это делает сам клиент.
/// </summary>
public sealed class ServerPingService
{
    private const int DefaultPort = 25565;
    private const int ProtocolAny = -1;

    public async Task<ServerStatus> PingAsync(string address, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var (host, port) = ParseAddress(address);
            (host, port) = await ResolveSrvAsync(host, port, ct).ConfigureAwait(false);

            using var tcp = new TcpClient { NoDelay = true, ReceiveTimeout = 8000, SendTimeout = 8000 };

            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));
                await tcp.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
            }

            await using var stream = tcp.GetStream();

            // --- Handshake (packet id 0x00, next state = 1 status) ---
            var handshake = new MemoryStream();
            WriteVarInt(handshake, 0x00);
            WriteVarInt(handshake, ProtocolAny);
            WriteString(handshake, host);
            handshake.WriteByte((byte)(port >> 8));
            handshake.WriteByte((byte)(port & 0xFF));
            WriteVarInt(handshake, 1);
            await WritePacketAsync(stream, handshake.ToArray(), ct).ConfigureAwait(false);

            // --- Status request (packet id 0x00, пустой) ---
            var request = new MemoryStream();
            WriteVarInt(request, 0x00);
            await WritePacketAsync(stream, request.ToArray(), ct).ConfigureAwait(false);

            // --- Ответ ---
            var payload = await ReadPacketAsync(stream, ct).ConfigureAwait(false);
            using var ms = new MemoryStream(payload);

            var packetId = ReadVarInt(ms);
            if (packetId != 0x00)
                return ServerStatus.Offline($"Неожиданный ответ сервера (id 0x{packetId:X2}).");

            var json = ReadString(ms);
            sw.Stop();

            return ParseStatusJson(json, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ServerStatus.Offline("Превышено время ожидания ответа.");
        }
        catch (SocketException ex)
        {
            return ServerStatus.Offline(DescribeSocketError(ex));
        }
        catch (Exception ex)
        {
            return ServerStatus.Offline("Ошибка: " + ex.Message);
        }
    }

    private static string DescribeSocketError(SocketException ex) => ex.SocketErrorCode switch
    {
        SocketError.HostNotFound => "Домен не найден.",
        SocketError.ConnectionRefused => "Сервер отклонил подключение (выключен?).",
        SocketError.TimedOut => "Сервер не отвечает.",
        SocketError.NetworkUnreachable or SocketError.HostUnreachable => "Сеть недоступна.",
        _ => "Не удалось подключиться: " + ex.SocketErrorCode
    };

    private static ServerStatus ParseStatusJson(string json, long pingMs)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var online = 0;
        var max = 0;
        if (root.TryGetProperty("players", out var players))
        {
            if (players.TryGetProperty("online", out var o) && o.TryGetInt32(out var ov)) online = ov;
            if (players.TryGetProperty("max", out var m) && m.TryGetInt32(out var mv)) max = mv;
        }

        var versionName = "";
        var protocol = 0;
        if (root.TryGetProperty("version", out var version))
        {
            versionName = version.TryGetProperty("name", out var vn) ? vn.GetString() ?? "" : "";
            if (version.TryGetProperty("protocol", out var pr) && pr.TryGetInt32(out var pv)) protocol = pv;
        }

        var motd = root.TryGetProperty("description", out var desc) ? ExtractText(desc) : "";

        byte[]? favicon = null;
        if (root.TryGetProperty("favicon", out var fav))
        {
            var s = fav.GetString();
            const string marker = "base64,";
            var idx = s?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
            if (s is not null && idx >= 0)
            {
                try { favicon = Convert.FromBase64String(s[(idx + marker.Length)..].Trim()); }
                catch { favicon = null; }
            }
        }

        return new ServerStatus
        {
            Online = true,
            OnlinePlayers = online,
            MaxPlayers = max,
            VersionName = versionName,
            ProtocolVersion = protocol,
            Motd = motd.Trim(),
            PingMs = pingMs,
            FaviconPng = favicon
        };
    }

    /// <summary>MOTD может быть строкой либо деревом chat-компонентов.</summary>
    private static string ExtractText(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                return StripFormatting(el.GetString() ?? "");

            case JsonValueKind.Object:
            {
                var sb = new StringBuilder();
                if (el.TryGetProperty("text", out var t)) sb.Append(StripFormatting(t.GetString() ?? ""));
                if (el.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Array)
                    foreach (var child in extra.EnumerateArray()) sb.Append(ExtractText(child));
                return sb.ToString();
            }

            case JsonValueKind.Array:
            {
                var sb = new StringBuilder();
                foreach (var child in el.EnumerateArray()) sb.Append(ExtractText(child));
                return sb.ToString();
            }

            default:
                return "";
        }
    }

    /// <summary>Убирает цветовые коды §a, §l и т.д.</summary>
    private static string StripFormatting(string s)
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

    // ---------------- Адрес и SRV ----------------

    public static (string host, int port) ParseAddress(string address)
    {
        var a = (address ?? "").Trim();
        if (a.Length == 0) throw new ArgumentException("Пустой адрес сервера.");

        // IPv6 в скобках
        if (a.StartsWith('['))
        {
            var close = a.IndexOf(']');
            if (close > 0)
            {
                var h = a[1..close];
                var rest = a[(close + 1)..];
                if (rest.StartsWith(':') && int.TryParse(rest[1..], out var p6)) return (h, p6);
                return (h, DefaultPort);
            }
        }

        var idx = a.LastIndexOf(':');
        if (idx > 0 && int.TryParse(a[(idx + 1)..], out var port))
            return (a[..idx], port);

        return (a, DefaultPort);
    }

    /// <summary>Ищет SRV-запись; если её нет — возвращает исходные host/port.</summary>
    private static async Task<(string host, int port)> ResolveSrvAsync(string host, int port, CancellationToken ct)
    {
        // SRV смотрим только если порт не задан явно и это не IP-адрес
        if (port != DefaultPort) return (host, port);
        if (System.Net.IPAddress.TryParse(host, out _)) return (host, port);

        try
        {
            var srv = await Task.Run(() => QuerySrvViaNslookup(host), ct).ConfigureAwait(false);
            if (srv is not null) return srv.Value;
        }
        catch { /* SRV необязателен */ }

        return (host, port);
    }

    /// <summary>
    /// .NET не умеет запрашивать SRV кроссплатформенно, поэтому используем nslookup.
    /// Ошибки не критичны — просто падаем обратно на A-запись.
    /// </summary>
    private static (string host, int port)? QuerySrvViaNslookup(string host)
    {
        var psi = new ProcessStartInfo("nslookup", $"-type=SRV _minecraft._tcp.{host}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        using var p = Process.Start(psi);
        if (p is null) return null;

        var output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(4000)) { try { p.Kill(); } catch { } return null; }

        string? target = null;
        var srvPort = DefaultPort;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();

            var portIdx = line.IndexOf("port", StringComparison.OrdinalIgnoreCase);
            if (portIdx >= 0 && line.Contains('='))
            {
                var val = line[(line.IndexOf('=', portIdx) + 1)..].Trim();
                if (int.TryParse(val, out var pv)) srvPort = pv;
            }

            var hostIdx = line.IndexOf("hostname", StringComparison.OrdinalIgnoreCase);
            if (hostIdx < 0) hostIdx = line.IndexOf("svr hostname", StringComparison.OrdinalIgnoreCase);
            if (hostIdx >= 0 && line.Contains('='))
            {
                target = line[(line.IndexOf('=', hostIdx) + 1)..].Trim().TrimEnd('.');
            }
        }

        return string.IsNullOrWhiteSpace(target) ? null : (target!, srvPort);
    }

    // ---------------- Протокол ----------------

    private static async Task WritePacketAsync(NetworkStream stream, byte[] data, CancellationToken ct)
    {
        var header = new MemoryStream();
        WriteVarInt(header, data.Length);
        var prefix = header.ToArray();

        await stream.WriteAsync(prefix, ct).ConfigureAwait(false);
        await stream.WriteAsync(data, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken ct)
    {
        var length = await ReadVarIntAsync(stream, ct).ConfigureAwait(false);
        if (length <= 0 || length > 8 * 1024 * 1024)
            throw new InvalidDataException($"Некорректная длина пакета: {length}");

        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, length - read), ct).ConfigureAwait(false);
            if (n <= 0) throw new EndOfStreamException("Соединение закрыто сервером.");
            read += n;
        }
        return buffer;
    }

    private static void WriteVarInt(Stream s, int value)
    {
        var v = unchecked((uint)value);
        while (true)
        {
            if ((v & ~0x7Fu) == 0) { s.WriteByte((byte)v); return; }
            s.WriteByte((byte)((v & 0x7F) | 0x80));
            v >>= 7;
        }
    }

    private static int ReadVarInt(Stream s)
    {
        var result = 0;
        var shift = 0;
        while (true)
        {
            var b = s.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift >= 35) throw new InvalidDataException("VarInt слишком длинный.");
        }
    }

    private static async Task<int> ReadVarIntAsync(NetworkStream s, CancellationToken ct)
    {
        var result = 0;
        var shift = 0;
        var one = new byte[1];

        while (true)
        {
            var n = await s.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n <= 0) throw new EndOfStreamException();

            result |= (one[0] & 0x7F) << shift;
            if ((one[0] & 0x80) == 0) return result;
            shift += 7;
            if (shift >= 35) throw new InvalidDataException("VarInt слишком длинный.");
        }
    }

    private static void WriteString(Stream s, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(s, bytes.Length);
        s.Write(bytes, 0, bytes.Length);
    }

    private static string ReadString(Stream s)
    {
        var len = ReadVarInt(s);
        if (len < 0 || len > 4 * 1024 * 1024) throw new InvalidDataException("Некорректная длина строки.");

        var buffer = new byte[len];
        var read = 0;
        while (read < len)
        {
            var n = s.Read(buffer, read, len - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
        return Encoding.UTF8.GetString(buffer);
    }
}
