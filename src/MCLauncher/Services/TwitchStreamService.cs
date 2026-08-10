using System.Net.Http.Headers;
using System.Text.Json;

namespace MCLauncher.Services;

public sealed class TwitchStreamService : IDisposable
{
    private const string ClientId = "1w4str5herfmk8s6ugx6qbh12y95yi";
    private const string TargetChannel = "moysecamm_tw";

    private readonly HttpClient _http;
    private Timer? _pollTimer;
    private bool _wasLive;
    private TwitchAccount? _account;

    public event Action<TwitchStreamInfo?>? StreamStatusChanged;

    public TwitchStreamService(HttpClient http)
    {
        _http = http;
    }

    public void StartMonitoring(TwitchAccount? account)
    {
        _account = account;
        _wasLive = false;
        _pollTimer?.Dispose();
        _pollTimer = null;

        if (account == null) return;

        _pollTimer = new Timer(async _ => await CheckStreamAsync().ConfigureAwait(false), null,
            TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    public void StopMonitoring()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    public async Task<TwitchStreamInfo?> GetStreamInfoAsync()
    {
        if (_account == null) return null;
        return await FetchStreamInfoAsync().ConfigureAwait(false);
    }

    private async Task CheckStreamAsync()
    {
        var info = await FetchStreamInfoAsync().ConfigureAwait(false);
        var isLive = info?.IsLive ?? false;

        if (isLive && !_wasLive)
        {
            _wasLive = true;
            StreamStatusChanged?.Invoke(info);
        }
        else if (!isLive)
        {
            _wasLive = false;
        }
    }

    private async Task<TwitchStreamInfo?> FetchStreamInfoAsync()
    {
        try
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _account!.AccessToken);
            _http.DefaultRequestHeaders.Add("Client-Id", ClientId);

            var response = await _http.GetAsync(
                $"https://api.twitch.tv/helix/streams?user_login={TargetChannel}").ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
            {
                return new TwitchStreamInfo { IsLive = false, ChannelName = TargetChannel };
            }

            var stream = data[0];
            return new TwitchStreamInfo
            {
                IsLive = true,
                Title = stream.GetProperty("title").GetString() ?? "",
                GameName = stream.GetProperty("game_name").GetString() ?? "",
                ThumbnailUrl = stream.GetProperty("thumbnail_url").GetString()?.Replace("{width}", "480").Replace("{height}", "270") ?? "",
                ViewerCount = stream.GetProperty("viewer_count").GetInt32(),
                StartedAt = stream.TryGetProperty("started_at", out var sa)
                    ? DateTimeOffset.Parse(sa.GetString() ?? DateTimeOffset.UtcNow.ToString())
                    : DateTimeOffset.UtcNow,
                ChannelName = TargetChannel
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"Twitch stream check error: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        StopMonitoring();
    }
}
