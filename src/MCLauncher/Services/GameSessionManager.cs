using System.Collections.Concurrent;
using System.Diagnostics;
using MCLauncher.Models;

namespace MCLauncher.Services;

/// <summary>Одна запущенная игра.</summary>
public sealed class GameSession
{
    public required Process Process { get; init; }
    public required string VersionId { get; init; }
    public required string InstanceId { get; init; }
    public required string InstanceName { get; init; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public int Pid { get; init; }
    public bool IsRunning
    {
        get
        {
            try { return !Process.HasExited; }
            catch { return false; }
        }
    }

    public TimeSpan Uptime => DateTimeOffset.Now - StartedAt;

    public string UptimeDisplay
    {
        get
        {
            var t = Uptime;
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes:00}:{t.Seconds:00}";
        }
    }
}

/// <summary>
/// Отслеживает запущенные экземпляры игры: не даёт плодить копии,
/// позволяет корректно остановить или принудительно закрыть игру.
/// </summary>
public sealed class GameSessionManager
{
    private readonly ConcurrentDictionary<int, GameSession> _sessions = new();

    public event Action? Changed;
    public event Action<GameSession, int>? SessionExited;

    public IReadOnlyCollection<GameSession> Sessions => _sessions.Values.ToList();

    public int RunningCount => _sessions.Values.Count(s => s.IsRunning);
    public bool AnyRunning => RunningCount > 0;

    public bool IsInstanceRunning(string instanceId) =>
        _sessions.Values.Any(s => s.IsRunning &&
            string.Equals(s.InstanceId, instanceId, StringComparison.Ordinal));

    public GameSession? GetByInstance(string instanceId) =>
        _sessions.Values.FirstOrDefault(s => s.IsRunning &&
            string.Equals(s.InstanceId, instanceId, StringComparison.Ordinal));

    public void Register(GameSession session)
    {
        _sessions[session.Pid] = session;

        try
        {
            session.Process.EnableRaisingEvents = true;
            session.Process.Exited += (_, _) =>
            {
                var code = 0;
                try { code = session.Process.ExitCode; } catch { }

                _sessions.TryRemove(session.Pid, out _);
                Log.Info($"Игра {session.VersionId} (PID {session.Pid}) завершилась с кодом {code}.");

                SessionExited?.Invoke(session, code);
                Changed?.Invoke();
            };
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось подписаться на завершение процесса: " + ex.Message);
        }

        Changed?.Invoke();
    }

    /// <summary>Мягко просит игру закрыться, при отказе — завершает принудительно.</summary>
    public async Task<bool> StopAsync(GameSession session, bool force = false, CancellationToken ct = default)
    {
        try
        {
            if (!session.IsRunning) return true;

            if (!force)
            {
                Log.Info($"Закрываю игру (PID {session.Pid})...");
                try { session.Process.CloseMainWindow(); } catch { }

                var exited = await Task.Run(() => session.Process.WaitForExit(6000), ct).ConfigureAwait(false);
                if (exited) return true;

                Log.Warn("Игра не отреагировала на запрос закрытия — завершаю принудительно.");
            }

            session.Process.Kill(entireProcessTree: true);
            await Task.Run(() => session.Process.WaitForExit(8000), ct).ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException)
        {
            return true; // процесс уже завершён
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось остановить игру", ex);
            return false;
        }
        finally
        {
            _sessions.TryRemove(session.Pid, out _);
            Changed?.Invoke();
        }
    }

    public async Task StopAllAsync(bool force = true, CancellationToken ct = default)
    {
        foreach (var s in Sessions)
            await StopAsync(s, force, ct).ConfigureAwait(false);
    }

    /// <summary>Убирает записи о процессах, умерших без события Exited.</summary>
    public void Prune()
    {
        foreach (var kv in _sessions)
        {
            if (!kv.Value.IsRunning) _sessions.TryRemove(kv.Key, out _);
        }
    }
}
