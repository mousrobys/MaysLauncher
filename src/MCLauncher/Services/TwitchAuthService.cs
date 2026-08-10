using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

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
    private const string RedirectUri = "http://localhost:8080/";
    private const string AuthUrl = "https://id.twitch.tv/oauth2/authorize";
    private const int ListenerPort = 8080;

    private HttpListener? _listener;
    private TaskCompletionSource<string>? _tcs;

    public bool IsLoggingIn { get; private set; }

    public async Task<TwitchAccount?> AuthenticateAsync()
    {
        if (IsLoggingIn) return null;
        IsLoggingIn = true;
        _tcs = new TaskCompletionSource<string>();

        try
        {
            StartLocalServer();
            OpenBrowser();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            cts.Token.Register(() => _tcs.TrySetResult(""));
            var token = await _tcs.Task.ConfigureAwait(false);

            if (string.IsNullOrEmpty(token)) return null;
            return await FetchUserProfileAsync(token).ConfigureAwait(false);
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
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://localhost:8080/");
            _listener.Start();
            Log.Info("Twitch callback server started on port 8080");
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to start HttpListener: {ex.Message}");
            _tcs?.TrySetResult("");
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                var request = context.Request;
                var url = request.Url!;

                string? token = null;

                if (url.Query.Contains("token="))
                {
                    var query = url.Query.TrimStart('?');
                    foreach (var pair in query.Split('&'))
                    {
                        var kv = pair.Split('=');
                        if (kv.Length == 2 && kv[0] == "token")
                        {
                            token = Uri.UnescapeDataString(kv[1]);
                            break;
                        }
                    }
                }

                if (token == null)
                {
                    var html = GetInterceptPage();
                    var buffer = Encoding.UTF8.GetBytes(html);
                    context.Response.ContentType = "text/html; charset=utf-8";
                    context.Response.ContentLength64 = buffer.Length;
                    await context.Response.OutputStream.WriteAsync(buffer).ConfigureAwait(false);
                    context.Response.Close();
                }
                else
                {
                    var html = GetSuccessPage();
                    var buffer = Encoding.UTF8.GetBytes(html);
                    context.Response.ContentType = "text/html; charset=utf-8";
                    context.Response.ContentLength64 = buffer.Length;
                    await context.Response.OutputStream.WriteAsync(buffer).ConfigureAwait(false);
                    context.Response.Close();
                    _tcs?.TrySetResult(token);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Twitch callback error: {ex.Message}");
                _tcs?.TrySetResult("");
            }
        });
    }

    private static string GetInterceptPage()
    {
        return @"<!DOCTYPE html><html><head><meta charset='utf-8'><title>Twitch Auth</title></head>
<body style='font-family:Segoe UI,sans-serif;background:#0e0e10;color:#fff;text-align:center;padding-top:80px'>
<h1 style='color:#9146ff'>Перехват токена...</h1>
<p id='status'>Ожидание ответа от Twitch...</p>
<script>
(function() {
    var hash = window.location.hash.substring(1);
    if (hash && hash.indexOf('access_token=') !== -1) {
        var params = new URLSearchParams(hash);
        var token = params.get('access_token');
        if (token) {
            document.getElementById('status').textContent = 'Токен получен! Отправка...';
            window.location.href = '/?token=' + encodeURIComponent(token);
            return;
        }
    }
    document.getElementById('status').textContent = 'Ожидание токена... (hash: ' + hash + ')';
})();
</script>
</body></html>";
    }

    private static string GetSuccessPage()
    {
        return @"<!DOCTYPE html><html><head><meta charset='utf-8'><title>Twitch Auth</title>
<style>body{font-family:Segoe UI,sans-serif;background:#0e0e10;color:#fff;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;text-align:center}
.container{padding:40px;border-radius:12px;background:#18181b}h1{color:#9146ff;margin-bottom:8px}p{color:#adadb8;margin-bottom:24px}
.btn{display:inline-block;background:#9146ff;color:#fff;text-decoration:none;padding:14px 32px;border-radius:8px;font-weight:600;font-size:16px}
.btn:hover{background:#772ce8}</style></head><body><div class='container'>
<h1>Авторизация успешна!</h1><p>Токен получен. Можно вернуться в лаунчер.</p>
<a class='btn' href='#' onclick='window.close()'>Вернуться в приложение</a>
</div></body></html>";
    }

    private void StopLocalServer()
    {
        try { _listener?.Stop(); _listener?.Close(); }
        catch { }
        _listener = null;
    }

    private static void OpenBrowser()
    {
        const string clientId = "1w4str5herfmk8s6ugx6qbh12y95yi";
        const string redirectUri = "http://localhost:8080/";
        var scope = Uri.EscapeDataString("user:read:email");
        var authUrl = $"https://id.twitch.tv/oauth2/authorize?client_id={clientId}&redirect_uri={Uri.EscapeDataString(redirectUri)}&response_type=token&scope={scope}";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = authUrl,
                UseShellExecute = true
            });
            Log.Info($"Opening Twitch auth URL: {authUrl}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to open browser: {ex.Message}");
        }
    }

    private static async Task<TwitchAccount?> FetchUserProfileAsync(string accessToken)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
            http.DefaultRequestHeaders.Add("Client-Id", ClientId);
            http.DefaultRequestHeaders.Add("User-Agent", "MaysLauncher/1.0 (+windows)");

            var response = await http.GetAsync("https://api.twitch.tv/helix/users").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                Log.Warn($"Twitch API error: {response.StatusCode} - {error}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
            {
                Log.Warn("Twitch API: no user data returned");
                return null;
            }

            var user = data[0];
            var login = user.GetProperty("login").GetString() ?? "";
            var displayName = user.GetProperty("display_name").GetString() ?? login;
            var userId = user.GetProperty("id").GetString() ?? "";
            var avatar = user.TryGetProperty("profile_image_url", out var img) ? img.GetString() ?? "" : "";

            return new TwitchAccount
            {
                Username = displayName,
                UserId = userId,
                AccessToken = accessToken,
                ProfileImageUrl = avatar,
                AuthenticatedAt = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"Twitch fetch user error: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        StopLocalServer();
    }
}
