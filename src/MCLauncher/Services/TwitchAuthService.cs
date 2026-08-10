using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MCLauncher.Services;

public class TwitchAccount
{
    public string Username { get; set; } = "";
    public string UserId { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string ProfileImageUrl { get; set; } = "";
    public DateTimeOffset AuthenticatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class TwitchStreamInfo
{
    public bool IsLive { get; set; }
    public string Title { get; set; } = "";
    public string GameName { get; set; } = "";
    public string ThumbnailUrl { get; set; } = "";
    public int ViewerCount { get; set; }
    public string StreamUrl => $"https://twitch.tv/{ChannelName}";
    public string ChannelName { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
}

public sealed class TwitchAuthService : IDisposable
{
    private const string ClientId = "1w4str5herfmk8s6ugx6qbh12y95yi";
    private const string RedirectUri = "http://localhost:8080";
    private const string AuthUrl = "https://id.twitch.tv/oauth2/authorize";
    private const string TokenUrl = "https://id.twitch.tv/oauth2/validate";
    private const int ListenerPort = 8080;

    private HttpListener? _listener;
    private TaskCompletionSource<string>? _tcs;
    private CancellationTokenSource? _cts;

    public bool IsLoggingIn { get; private set; }

    public async Task<TwitchAccount?> AuthenticateAsync()
    {
        if (IsLoggingIn) return null;
        IsLoggingIn = true;
        _tcs = new TaskCompletionSource<string>();
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        try
        {
            StartLocalServer();
            OpenBrowser();

            var token = await _tcs.Task.ConfigureAwait(false);
            if (string.IsNullOrEmpty(token)) return null;

            return await ValidateAndBuildAccountAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Twitch auth failed: {ex.Message}");
            return null;
        }
        finally
        {
            StopLocalServer();
            IsLoggingIn = false;
        }
    }

    private void StartLocalServer()
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"{RedirectUri}/");
        _listener.Start();

        Task.Run(async () =>
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                var request = context.Request;
                var token = ExtractTokenFromUrl(request.Url?.ToString() ?? "");

                var response = context.Response;
                var html = "<html><body><script>window.close()</script><h2>Авторизация успешна! Можно закрыть это окно.</h2></body></html>";
                var buffer = Encoding.UTF8.GetBytes(html);
                response.ContentLength64 = buffer.Length;
                response.ContentType = "text/html; charset=utf-8";
                await response.OutputStream.WriteAsync(buffer).ConfigureAwait(false);
                response.Close();

                _tcs?.TrySetResult(token ?? "");
            }
            catch (Exception ex)
            {
                Log.Warn($"Twitch callback error: {ex.Message}");
                _tcs?.TrySetResult("");
            }
        });
    }

    private string? ExtractTokenFromUrl(string url)
    {
        var match = Regex.Match(url, @"access_token=([^&]+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private void StopLocalServer()
    {
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch { }
        _listener = null;
    }

    private static void OpenBrowser()
    {
        var scope = Uri.EscapeDataString("user:read:email");
        var state = Guid.NewGuid().ToString("N")[..16];
        var url = $"{AuthUrl}?client_id={ClientId}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&response_type=token&scope={scope}&state={state}";

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to open browser: {ex.Message}");
        }
    }

    private static async Task<TwitchAccount?> ValidateAndBuildAccountAsync(string accessToken)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("Authorization", $"OAuth {accessToken}");
            http.DefaultRequestHeaders.Add("User-Agent", "MaysLauncher/1.0");

            var response = await http.GetAsync("https://id.twitch.tv/oauth2/validate").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var doc = JsonDocument.Parse(json);

            var login = doc.RootElement.GetProperty("login").GetString() ?? "";
            var userId = doc.RootElement.GetProperty("user_id").GetString() ?? "";

            string avatar = "";
            try
            {
                using var http2 = new HttpClient();
                http2.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
                http2.DefaultRequestHeaders.Add("Client-Id", ClientId);
                var resp = await http2.GetAsync($"https://api.twitch.tv/helix/users?id={userId}").ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var j = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var d = JsonDocument.Parse(j);
                    if (d.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
                    {
                        avatar = data[0].GetProperty("profile_image_url").GetString() ?? "";
                    }
                }
            }
            catch { }

            return new TwitchAccount
            {
                Username = login,
                UserId = userId,
                AccessToken = accessToken,
                ProfileImageUrl = avatar
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"Twitch validate error: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        StopLocalServer();
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
