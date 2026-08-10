using System.Net.Http.Headers;
using System.Text.Json;

namespace MCLauncher.Services;

public sealed class TwitchStreamService : IDisposable
{
    private const string ClientId = "1w4str5herfmk8s6ugx6qbh12y95yi";
    private const string Channel = "moysecamm_tw";

    private readonly HttpClient _http;
    private Timer? _timer;
    private bool _wasLive;

    public event Action<TwitchStreamInfo?>? OnStatusChanged;

    public TwitchStreamService(HttpClient http)
    {
        _http = http;
    }

    public void Start(TwitchAccount? account)
    {
        Stop();
        if (account == null) return;

        _timer = new Timer(async _ => await CheckAsync().ConfigureAwait(false),
            null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _wasLive = false;
    }

    public async Task<TwitchStreamInfo?> GetStreamInfoAsync()
    {
        if (_http.DefaultRequestHeaders.Authorization == null) return null;
        return await FetchAsync().ConfigureAwait(false);
    }

    private async Task CheckAsync()
    {
        var info = await FetchAsync().ConfigureAwait(false);
        var live = info?.IsLive ?? false;

        if (live && !_wasLive)
        {
            _wasLive = true;
            OnStatusChanged?.Invoke(info);
        }
        else if (!live)
        {
            _wasLive = false;
        }
    }

    private async Task<TwitchStreamInfo?> FetchAsync()
    {
        try
        {
            var resp = await _http.GetAsync(
                $"https://api.twitch.tv/helix/streams?user_login={Channel}").ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
            {
                return new TwitchStreamInfo { IsLive = false, ChannelName = Channel };
            }

            var s = data[0];
            return new TwitchStreamInfo
            {
                IsLive = true,
                Title = s.GetProperty("title").GetString() ?? "",
                GameName = s.GetProperty("game_name").GetString() ?? "",
                ViewerCount = s.GetProperty("viewer_count").GetInt32(),
                StartedAt = s.GetProperty("started_at").GetString() ?? "",
                ChannelName = Channel
            };
        }
        catch { return null; }
    }

    public void Dispose() => Stop();
}
