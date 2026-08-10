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

            Task.Run(async () =>
            {
                try
                {
                    var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    var url = ctx.Request.Url!;
                    string? token = null;

                    string urlStr = url.ToString();
                    int fragIdx = urlStr.IndexOf('#');
                    if (fragIdx >= 0)
                    {
                        string fragment = urlStr.Substring(fragIdx + 1);
                        foreach (var pair in fragment.Split('&'))
                        {
                            var kv = pair.Split('=');
                            if (kv.Length == 2 && kv[0] == "access_token")
                            {
                                token = Uri.UnescapeDataString(kv[1]);
                                break;
                            }
                        }
                    }

                    if (token == null && url.Query.Contains("access_token="))
                    {
                        var query = url.Query.TrimStart('?');
                        foreach (var pair in query.Split('&'))
                        {
                            var kv = pair.Split('=');
                            if (kv.Length == 2 && kv[0] == "access_token")
                            {
                                token = Uri.UnescapeDataString(kv[1]);
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

                    byte[] buf = Encoding.UTF8.GetBytes(html);
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = buf.Length;
                    await ctx.Response.OutputStream.WriteAsync(buf);
                    ctx.Response.Close();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Twitch callback error: {ex.Message}");
                    _tcs?.TrySetResult("");
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
        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head>" +
               "<body style=\"font-family:Segoe UI;background:#0e0e10;color:#fff;text-align:center;padding-top:80px\">" +
               "<h1 style=\"color:#9146FF\">Waiting for Twitch token...</h1>" +
               "<script>var h=location.hash.substring(1);if(h&&h.includes('access_token=')){var p=new URLSearchParams(h);var t=p.get('access_token');if(t)location.href='/?access_token='+encodeURIComponent(t);}</script>" +
               "</body></html>";
    }

    private static string GetSuccessPage()
    {
        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head>" +
               "<body style=\"font-family:Segoe UI;background:#0e0e10;color:#fff;text-align:center;padding-top:80px\">" +
               "<h1 style=\"color:#9146FF\">Authorization successful!</h1>" +
               "<p>You can close this tab and return to the launcher.</p>" +
               "<button onclick=\"window.close()\" style=\"background:#9146FF;color:#fff;border:none;padding:12px 24px;border-radius:6px;font-size:14px;cursor:pointer\">Close</button>" +
               "</body></html>";
    }

    private static void OpenTwitchAuthPage()
    {
        const string clientId = "1w4str5herfmk8s6ugx6qbh12y95yi";
        var url = $"https://id.twitch.tv/oauth2/authorize?client_id={clientId}&redirect_uri={Uri.EscapeDataString("http://localhost:8080/")}&response_type=token&scope={Uri.EscapeDataString("user:read:email")}";

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"OpenBrowser error: {ex.Message}");
        }
    }

    private void StopListener()
    {
        try { _listener?.Stop(); _listener?.Close(); }
        catch { }
        _listener = null;
    }

    private static async Task<TwitchAccount?> FetchUserProfileAsync(string accessToken)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
            http.DefaultRequestHeaders.Add("Client-Id", "1w4str5herfmk8s6ugx6qbh12y95yi");
            http.DefaultRequestHeaders.Add("User-Agent", "MaysLauncher/1.0");

            var resp = await http.GetAsync("https://api.twitch.tv/helix/users").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"Twitch API error: {resp.StatusCode}");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
                return null;

            var user = data[0];
            return new TwitchAccount
            {
                Username = user.GetProperty("display_name").GetString() ?? user.GetProperty("login").GetString() ?? "",
                UserId = user.GetProperty("id").GetString() ?? "",
                AccessToken = accessToken,
                ProfileImageUrl = user.TryGetProperty("profile_image_url", out var img) ? img.GetString() ?? "" : "",
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
