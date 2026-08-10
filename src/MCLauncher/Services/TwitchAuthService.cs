using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace MCLauncher.Services;

public class TwitchAccount
{
    public string Username = "";
    public string UserId = "";
    public string AccessToken = "";
    public string ProfileImageUrl = "";
    public string AuthenticatedAt = DateTimeOffset.UtcNow.ToString("o");
}

public class TwitchStreamInfo
{
    public bool IsLive;
    public string Title = "";
    public string GameName = "";
    public string ThumbnailUrl = "";
    public int ViewerCount;
    public string ChannelName = "";
    public string StartedAt = "";
    public string StreamUrl => $"https://twitch.tv/{ChannelName}";
}

public sealed class TwitchAuthService : IDisposable
{
    private const string ClientId = "1w4str5herfmk8s6ugx6qbh12y95yi";
    private HttpListener? _listener;
    private TaskCompletionSource<string>? _tcs;
    public bool IsLoggingIn;

    public async Task<TwitchAccount?> AuthenticateAsync()
    {
        if (IsLoggingIn) return null;
        IsLoggingIn = true;
        _tcs = new TaskCompletionSource<string>();

        try
        {
            StartListener();
            OpenTwitchAuthPage();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            cts.Token.Register(() => _tcs.TrySetResult(""));
            var token = await _tcs.Task.ConfigureAwait(false);

            if (string.IsNullOrEmpty(token)) return null;
            return await FetchUserProfileAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Twitch auth error: {ex.Message}");
            return null;
        }
        finally
        {
            StopListener();
            IsLoggingIn = false;
        }
    }

    private void StartListener()
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://localhost:8080/");
            _listener.Start();
            Log.Info("Twitch listener started on port 8080");

            Task.Run(async () =>
            {
                while (_listener!.IsListening)
                {
                    try
                    {
                        var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                        var request = ctx.Request;
                        var url = request.Url!;
                        var response = ctx.Response;

                        string? token = null;
                        string queryString = url.Query;

                        Log.Info($"Twitch callback received: {url.PathAndQuery}");

                        if (queryString.Contains("access_token="))
                        {
                            var query = queryString.TrimStart('?');
                            foreach (var pair in query.Split('&'))
                            {
                                var kv = pair.Split('=');
                                if (kv.Length == 2 && kv[0] == "access_token")
                                {
                                    token = Uri.UnescapeDataString(kv[1]);
                                    Log.Info("Access token extracted from query");
                                    break;
                                }
                            }
                        }

                        string html;
                        if (token != null)
                        {
                            html = GetSuccessPage();
                            _tcs?.TrySetResult(token);
                        }
                        else
                        {
                            html = GetInterceptPage();
                        }

                        byte[] buffer = Encoding.UTF8.GetBytes(html);
                        response.ContentType = "text/html; charset=utf-8";
                        response.ContentLength64 = buffer.Length;
                        await response.OutputStream.WriteAsync(buffer);
                        response.Close();

                        if (token != null) break;
                    }
                    catch (HttpListenerException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Listener error: {ex.Message}");
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"StartListener error: {ex.Message}");
        }
    }

    private static string GetInterceptPage()
    {
        return @"<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <title>Twitch Auth</title>
    <style>
        body { font-family: 'Segoe UI', sans-serif; background: #0e0e10; color: #fff; display: flex; align-items: center; justify-content: center; height: 100vh; margin: 0; }
        .container { padding: 40px; border-radius: 12px; background: #18181b; text-align: center; }
        h1 { color: #9146ff; }
        p { color: #adadb8; }
    </style>
</head>
<body>
    <div class=""container"">
        <h1>Waiting for Twitch...</h1>
        <p>If you see this page, something went wrong. Go back to the launcher and try again.</p>
    </div>
    <script>
        var hash = window.location.hash.substring(1);
        if (hash && hash.indexOf('access_token=') !== -1) {
            var params = new URLSearchParams(hash);
            var token = params.get('access_token');
            if (token) {
                window.location.href = '/?access_token=' + encodeURIComponent(token);
            }
        }
    </script>
</body>
</html>";
    }

    private static string GetSuccessPage()
    {
        return @"<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <title>Twitch Auth</title>
    <style>
        body { font-family: 'Segoe UI', sans-serif; background: #0e0e10; color: #fff; display: flex; align-items: center; justify-content: center; height: 100vh; margin: 0; }
        .container { padding: 40px; border-radius: 12px; background: #18181b; text-align: center; }
        h1 { color: #9146ff; }
        p { color: #adadb8; margin-bottom: 24px; }
        .btn { display: inline-block; background: #9146ff; color: #fff; text-decoration: none; padding: 14px 32px; border-radius: 8px; font-weight: 600; font-size: 16px; cursor: pointer; border: none; }
        .btn:hover { background: #772ce8; }
    </style>
</head>
<body>
    <div class=""container"">
        <h1>Authorization successful!</h1>
        <p>You can close this tab and return to the launcher.</p>
        <button class=""btn"" onclick=""window.close()"">Close this tab</button>
    </div>
</body>
</html>";
    }

    private static void OpenTwitchAuthPage()
    {
        var scope = Uri.EscapeDataString("user:read:email");
        var redirectUri = Uri.EscapeDataString("http://localhost:8080/");
        var url = $"https://id.twitch.tv/oauth2/authorize?client_id={ClientId}&redirect_uri={redirectUri}&response_type=token&scope={scope}";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            Log.Info($"Opened Twitch auth URL in browser");
        }
        catch (Exception ex)
        {
            Log.Warn($"OpenBrowser error: {ex.Message}");
        }
    }

    private void StopListener()
    {
        try
        {
            if (_listener != null)
            {
                _listener.Stop();
                _listener.Close();
                _listener = null;
                Log.Info("Twitch listener stopped");
            }
        }
        catch { }
    }

    private static async Task<TwitchAccount?> FetchUserProfileAsync(string accessToken)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
            http.DefaultRequestHeaders.Add("Client-Id", "1w4str5herfmk8s6ugx6qbh12y95yi");
            http.DefaultRequestHeaders.Add("User-Agent", "MaysLauncher/1.0");

            Log.Info($"Fetching Twitch user profile...");

            var resp = await http.GetAsync("https://api.twitch.tv/helix/users").ConfigureAwait(false);
            Log.Info($"Twitch API response: {resp.StatusCode}");

            if (!resp.IsSuccessStatusCode)
            {
                var errorBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Log.Warn($"Twitch API error: {resp.StatusCode} - {errorBody}");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
            {
                Log.Warn("Twitch API: no user data in response");
                return null;
            }

            var user = data[0];
            var login = user.TryGetProperty("login", out var l) ? l.GetString() ?? "" : "";
            var displayName = user.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? login : login;
            var userId = user.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
            var avatar = user.TryGetProperty("profile_image_url", out var img) ? img.GetString() ?? "" : "";

            Log.Info($"Twitch user: {displayName} ({login})");

            return new TwitchAccount
            {
                Username = displayName,
                UserId = userId,
                AccessToken = accessToken,
                ProfileImageUrl = avatar,
                AuthenticatedAt = DateTimeOffset.UtcNow.ToString("o")
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"FetchUserProfile error: {ex.Message}");
            return null;
        }
    }

    public void Dispose() => StopListener();
}
