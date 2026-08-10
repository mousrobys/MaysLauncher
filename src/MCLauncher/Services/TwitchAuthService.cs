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
            OpenBrowser();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            cts.Token.Register(() => _tcs.TrySetResult(""));
            var code = await _tcs.Task.ConfigureAwait(false);
            if (string.IsNullOrEmpty(code)) return null;

            var token = await ExchangeCodeAsync(code).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token)) return null;

            return await GetProfileAsync(token).ConfigureAwait(false);
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

            Task.Run(async () =>
            {
                try
                {
                    var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    var url = ctx.Request.Url!;
                    string? code = null;

                    if (url.Query.Contains("code="))
                    {
                        var query = url.Query.TrimStart('?');
                        foreach (var pair in query.Split('&'))
                        {
                            var kv = pair.Split('=');
                            if (kv.Length == 2 && kv[0] == "code")
                            {
                                code = Uri.UnescapeDataString(kv[1]);
                                break;
                            }
                        }
                    }

                    string html;
                    if (code != null)
                    {
                        html = GetSuccessHtml();
                        _tcs?.TrySetResult(code);
                    }
                    else
                    {
                        html = "<html><body>Waiting...</body></html>";
                    }

                    var bytes = Encoding.UTF8.GetBytes(html);
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                    ctx.Response.Close();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Listener error: {ex.Message}");
                    _tcs?.TrySetResult("");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"StartListener error: {ex.Message}");
        }
    }

    private static string GetSuccessHtml()
    {
        return "<!DOCTYPE html>\n" +
            "<html><head><meta charset=\"utf-8\"><title>Twitch</title>\n" +
            "<style>\n" +
            "body{font-family:Segoe UI,sans-serif;background:#0e0e10;color:#fff;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;text-align:center}\n" +
            ".box{padding:40px;border-radius:12px;background:#18181b}\n" +
            "h1{color:#9146ff;margin-bottom:8px}p{color:#adadb8;margin-bottom:24px}\n" +
            ".btn{display:inline-block;background:#9146ff;color:#fff;text-decoration:none;padding:14px 32px;border-radius:8px;font-weight:600;font-size:16px}\n" +
            ".btn:hover{background:#772ce8}\n" +
            "</style></head><body><div class=\"box\">\n" +
            "<h1>Авторизация успешна!</h1>\n" +
            "<p>Возвращайтесь в лаунчер.</p>\n" +
            "<a class=\"btn\" href=\"#\" onclick=\"window.close()\">Вернуться в приложение</a>\n" +
            "</div></body></html>";
    }

    private static void OpenBrowser()
    {
        var url = $"https://id.twitch.tv/oauth2/authorize?client_id={ClientId}&redirect_uri={Uri.EscapeDataString("http://localhost:8080/")}&response_type=code&scope={Uri.EscapeDataString("user:read:email")}";

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"OpenBrowser error: {ex.Message}");
        }
    }

    private static async Task<string?> ExchangeCodeAsync(string code)
    {
        try
        {
            using var http = new HttpClient();
            var body = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("client_id", ClientId),
                new KeyValuePair<string,string>("code", code),
                new KeyValuePair<string,string>("grant_type", "authorization_code"),
                new KeyValuePair<string,string>("redirect_uri", "http://localhost:8080/")
            });

            var resp = await http.PostAsync("https://id.twitch.tv/oauth2/token", body).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("access_token").GetString();
        }
        catch (Exception ex)
        {
            Log.Warn($"ExchangeCode error: {ex.Message}");
            return null;
        }
    }

    private static async Task<TwitchAccount?> GetProfileAsync(string token)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
            http.DefaultRequestHeaders.Add("Client-Id", ClientId);

            var resp = await http.GetAsync("https://api.twitch.tv/helix/users").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
                return null;

            var u = data[0];
            return new TwitchAccount
            {
                Username = u.GetProperty("display_name").GetString() ?? "",
                UserId = u.GetProperty("id").GetString() ?? "",
                AccessToken = token,
                ProfileImageUrl = u.TryGetProperty("profile_image_url", out var img) ? img.GetString() ?? "" : "",
                AuthenticatedAt = DateTimeOffset.UtcNow.ToString("o")
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"GetProfile error: {ex.Message}");
            return null;
        }
    }

    private void StopListener()
    {
        try { _listener?.Stop(); _listener?.Close(); }
        catch { }
        _listener = null;
    }

    public void Dispose() => StopListener();
}
