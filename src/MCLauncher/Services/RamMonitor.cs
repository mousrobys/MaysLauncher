using System.Diagnostics;
using System.Windows.Threading;

namespace MCLauncher.Services;

public class RamMonitor
{
    private readonly PerformanceCounter _counter;
    private readonly DispatcherTimer _timer;
    private readonly List<(DateTime Time, double UsedMb)> _history = new();
    private readonly int _maxHistory = 60;

    public event Action<(DateTime Time, double UsedMb)>? OnUpdate;
    public event Action<List<(DateTime Time, double UsedMb)>>? OnHistoryUpdated;

    public RamMonitor()
    {
        _counter = new PerformanceCounter("Memory", "Available MBytes");
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void Tick()
    {
        try
        {
            var availableMb = _counter.NextValue();
            var totalMb = GetTotalRamMb();
            var usedMb = totalMb - availableMb;

            var point = (DateTime.Now, usedMb);
            _history.Add(point);
            if (_history.Count > _maxHistory) _history.RemoveAt(0);

            OnUpdate?.Invoke(point);
            OnHistoryUpdated?.Invoke(_history.ToList());
        }
        catch { }
    }

    public static double GetTotalRamMb()
    {
        try
        {
            var totalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return totalBytes / (1024.0 * 1024.0);
        }
        catch
        {
            return 8192;
        }
    }

    public List<(DateTime Time, double UsedMb)> GetHistory() => _history.ToList();
}
