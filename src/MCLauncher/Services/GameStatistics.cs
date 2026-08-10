using System.IO;
using System.Text.Json;

namespace MCLauncher.Services;

public class GameStatistics
{
    public int TotalPlaySeconds { get; set; }
    public int TotalLaunches { get; set; }
    public string? LastInstanceId { get; set; }
    public string? LastInstanceName { get; set; }
    public DateTime LastPlayedAt { get; set; }
    public Dictionary<string, int> InstancePlayTime { get; set; } = new();

    private static string DataFile => Path.Combine(LauncherPaths.Root, "statistics.json");
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static GameStatistics Load()
    {
        try
        {
            if (System.IO.File.Exists(DataFile))
                return JsonSerializer.Deserialize<GameStatistics>(System.IO.File.ReadAllText(DataFile)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try { System.IO.File.WriteAllText(DataFile, JsonSerializer.Serialize(this, Opts)); }
        catch { }
    }

    public void RecordLaunch(string instanceId, string instanceName)
    {
        TotalLaunches++;
        LastInstanceId = instanceId;
        LastInstanceName = instanceName;
        LastPlayedAt = DateTime.UtcNow;
        Save();
    }

    public void RecordPlayTime(string instanceId, int seconds)
    {
        TotalPlaySeconds += seconds;
        if (!InstancePlayTime.ContainsKey(instanceId))
            InstancePlayTime[instanceId] = 0;
        InstancePlayTime[instanceId] += seconds;
        Save();
    }

    public string GetFormattedTotalTime()
    {
        var ts = TimeSpan.FromSeconds(TotalPlaySeconds);
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours} ч {ts.Minutes} м";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes} м";
        return $"{ts.TotalSeconds} с";
    }

    public string GetFormattedLastPlayed()
    {
        var diff = DateTime.UtcNow - LastPlayedAt;
        if (diff.TotalDays >= 1) return $"{(int)diff.TotalDays} д назад";
        if (diff.TotalHours >= 1) return $"{(int)diff.TotalHours} ч назад";
        if (diff.TotalMinutes >= 1) return $"{(int)diff.TotalMinutes} м назад";
        return "только что";
    }
}
