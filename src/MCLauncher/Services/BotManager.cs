using System.Collections.ObjectModel;

namespace MCLauncher.Services;

/// <summary>Один запущенный бот в списке.</summary>
public sealed class BotInstance
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required BotService Service { get; init; }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public bool IsRunning => Service.IsRunning;
    public bool InWorld { get; set; }

    public string Endpoint => $"{Host}:{Port}";

    public string UptimeDisplay
    {
        get
        {
            var t = DateTimeOffset.Now - StartedAt;
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes:00}:{t.Seconds:00}";
        }
    }
}

/// <summary>
/// Управляет несколькими ботами сразу — для сценок, где нужно
/// два-четыре персонажа в кадре.
/// </summary>
public sealed class BotManager
{
    private const int MaxBots = 6;

    private readonly HttpClient _http;
    private readonly List<BotInstance> _bots = new();

    public BotManager(HttpClient http) => _http = http;

    /// <summary>Вывод любого бота: (имя бота, строка).</summary>
    public event Action<string, string>? Output;
    public event Action? Changed;

    public IReadOnlyList<BotInstance> Bots
    {
        get { lock (_bots) return _bots.ToList(); }
    }

    public int RunningCount
    {
        get { lock (_bots) return _bots.Count(b => b.IsRunning); }
    }

    public bool AnyRunning => RunningCount > 0;

    public bool NameTaken(string name)
    {
        lock (_bots)
            return _bots.Any(b => b.IsRunning &&
                string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Ставит окружение один раз для всех ботов.</summary>
    public async Task EnsureEnvironmentAsync(
        Action<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var svc = new BotService(_http);
        svc.Output += line => Output?.Invoke("setup", line);
        await svc.EnsureEnvironmentAsync(progress, ct).ConfigureAwait(false);
    }

    public async Task<BotInstance> StartAsync(
        string host, int port, string name, string mcVersion, CancellationToken ct = default)
    {
        lock (_bots)
        {
            if (_bots.Count(b => b.IsRunning) >= MaxBots)
                throw new InvalidOperationException(
                    $"Больше {MaxBots} ботов одновременно запускать нельзя — " +
                    "каждый занимает память и слот на сервере.");

            if (_bots.Any(b => b.IsRunning &&
                    string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"Бот с ником «{name}» уже запущен. Ники должны отличаться.");
        }

        var service = new BotService(_http);

        var bot = new BotInstance
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Host = host,
            Port = port,
            Service = service
        };

        service.Output += line =>
        {
            if (line.Contains("появился в мире")) bot.InWorld = true;
            if (line.Contains("отключился")) bot.InWorld = false;

            Output?.Invoke(name, line);
        };

        service.RunningChanged += _ =>
        {
            Changed?.Invoke();
            PruneStopped();
        };

        lock (_bots) _bots.Add(bot);
        Changed?.Invoke();

        try
        {
            await service.StartAsync(host, port, name, mcVersion, ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_bots) _bots.Remove(bot);
            Changed?.Invoke();
            throw;
        }

        return bot;
    }

    /// <summary>Команда конкретному боту.</summary>
    public void Send(string botId, string command)
    {
        BotInstance? bot;
        lock (_bots) bot = _bots.FirstOrDefault(b => b.Id == botId);

        bot?.Service.Send(command);
    }

    /// <summary>Команда сразу всем — удобно для «все стоп».</summary>
    public void Broadcast(string command)
    {
        foreach (var b in Bots.Where(x => x.IsRunning))
            b.Service.Send(command);
    }

    public void Stop(string botId)
    {
        BotInstance? bot;
        lock (_bots) bot = _bots.FirstOrDefault(b => b.Id == botId);

        if (bot is null) return;

        bot.Service.Stop();
        lock (_bots) _bots.Remove(bot);
        Changed?.Invoke();
    }

    public void StopAll()
    {
        foreach (var b in Bots) b.Service.Stop();

        lock (_bots) _bots.Clear();
        Changed?.Invoke();
    }

    private void PruneStopped()
    {
        lock (_bots) _bots.RemoveAll(b => !b.IsRunning);
    }

    /// <summary>Подбирает свободный ник: MaysBot, MaysBot2, MaysBot3…</summary>
    public string SuggestName(string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "MaysBot";
        if (!NameTaken(baseName)) return baseName;

        for (var i = 2; i <= MaxBots + 2; i++)
        {
            var candidate = baseName.Length > 14 ? baseName[..14] + i : baseName + i;
            if (!NameTaken(candidate)) return candidate;
        }

        return baseName + Random.Shared.Next(10, 99);
    }
}
