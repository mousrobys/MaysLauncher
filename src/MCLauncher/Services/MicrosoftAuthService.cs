using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCLauncher.Models;

namespace MCLauncher.Services;

/// <summary>
/// Полная цепочка официальной авторизации:
/// Microsoft OAuth2 (PKCE, loopback redirect)
///   -> Xbox Live (user.auth.xboxlive.com)
///   -> XSTS (xsts.auth.xboxlive.com)
///   -> Minecraft Services (api.minecraftservices.com/authentication/login_with_xbox)
///   -> проверка владения игрой -> профиль (UUID + ник).
/// </summary>
public sealed class MicrosoftAuthService
{
    // Публичный client_id официального Minecraft-лаунчера (Azure AD, public client).
    // При желании подставьте свой из portal.azure.com (App registration -> Mobile & desktop, redirect http://localhost).
    public const string DefaultClientId = "00000000402b5328";

    private const string AuthorizeEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize";
    private const string TokenEndpoint     = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string DeviceCodeEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";

    private const string XblAuthUrl  = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsAuthUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string McLoginUrl  = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string McProfileUrl = "https://api.minecraftservices.com/minecraft/profile";
    private const string McEntitlementsUrl = "https://api.minecraftservices.com/entitlements/mcstore";

    private const string Scope = "XboxLive.signin offline_access";

    private readonly HttpClient _http;
    private readonly string _clientId;

    public MicrosoftAuthService(HttpClient http, string? clientId = null)
    {
        _http = http;
        _clientId = string.IsNullOrWhiteSpace(clientId) ? DefaultClientId : clientId!;
    }

    public event Action<string>? Status;
    private void Report(string s) => Status?.Invoke(s);

    // =====================================================================
    //  ПУБЛИЧНЫЕ МЕТОДЫ
    // =====================================================================

    /// <summary>Интерактивный вход: поднимает локальный HTTP-слушатель и открывает системный браузер.</summary>
    public async Task<MinecraftAccount> SignInInteractiveAsync(CancellationToken ct = default)
    {
        Report("Открываю браузер для входа Microsoft...");

        var (verifier, challenge) = CreatePkcePair();
        var state = RandomString(24);

        // Ищем свободный порт на loopback
        var (listener, redirectUri) = StartLoopbackListener();

        try
        {
            var authUrl =
                $"{AuthorizeEndpoint}?client_id={Uri.EscapeDataString(_clientId)}" +
                $"&response_type=code" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                $"&scope={Uri.EscapeDataString(Scope)}" +
                $"&state={state}" +
                $"&code_challenge={challenge}" +
                $"&code_challenge_method=S256" +
                $"&prompt=select_account";

            OpenBrowser(authUrl);

            var code = await WaitForAuthorizationCodeAsync(listener, state, ct).ConfigureAwait(false);

            Report("Получен код авторизации, обмениваю на токен...");
            var msToken = await RedeemAuthorizationCodeAsync(code, verifier, redirectUri, ct).ConfigureAwait(false);

            return await CompleteXboxChainAsync(msToken, ct).ConfigureAwait(false);
        }
        finally
        {
            try { listener.Stop(); listener.Close(); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Резервный способ входа — Device Code Flow (если браузерный редирект заблокирован).
    /// Вызывает <paramref name="onCode"/> с кодом и URL, который пользователь вводит вручную.
    /// </summary>
    public async Task<MinecraftAccount> SignInWithDeviceCodeAsync(
        Action<DeviceCodeResponse> onCode, CancellationToken ct = default)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["scope"] = Scope
        });

        using var resp = await _http.PostAsync(DeviceCodeEndpoint, form, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new AuthException($"Device code запрос отклонён ({(int)resp.StatusCode}): {body}");

        var dc = JsonSerializer.Deserialize<DeviceCodeResponse>(body)
                 ?? throw new AuthException("Пустой ответ device code.");

        onCode(dc);
        OpenBrowser(dc.VerificationUri);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(dc.ExpiresIn);
        var interval = Math.Max(3, dc.Interval);

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);

            var pollForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = _clientId,
                ["device_code"] = dc.DeviceCode
            });

            using var pr = await _http.PostAsync(TokenEndpoint, pollForm, ct).ConfigureAwait(false);
            var pbody = await pr.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (pr.IsSuccessStatusCode)
            {
                var tok = JsonSerializer.Deserialize<MicrosoftTokenResponse>(pbody)
                          ?? throw new AuthException("Пустой ответ токена.");
                return await CompleteXboxChainAsync(tok, ct).ConfigureAwait(false);
            }

            using var doc = JsonDocument.Parse(pbody);
            var err = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;

            switch (err)
            {
                case "authorization_pending": continue;
                case "slow_down": interval += 5; continue;
                case "expired_token": throw new AuthException("Код истёк. Попробуйте войти заново.");
                case "authorization_declined": throw new AuthException("Вход отменён пользователем.");
                default: throw new AuthException($"Ошибка device code: {pbody}");
            }
        }

        throw new AuthException("Время ожидания входа истекло.");
    }

    /// <summary>Тихое обновление сессии по refresh_token.</summary>
    public async Task<MinecraftAccount> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        Report("Обновляю сессию...");

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
            ["scope"] = Scope
        });

        using var resp = await _http.PostAsync(TokenEndpoint, form, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new AuthException($"Не удалось обновить токен ({(int)resp.StatusCode}). Войдите заново.");

        var tok = JsonSerializer.Deserialize<MicrosoftTokenResponse>(body)
                  ?? throw new AuthException("Пустой ответ обновления токена.");

        return await CompleteXboxChainAsync(tok, ct).ConfigureAwait(false);
    }

    // =====================================================================
    //  ЦЕПОЧКА XBOX -> MINECRAFT
    // =====================================================================

    private async Task<MinecraftAccount> CompleteXboxChainAsync(MicrosoftTokenResponse msToken, CancellationToken ct)
    {
        Report("Авторизация в Xbox Live...");
        var xbl = await AuthenticateWithXboxLiveAsync(msToken.AccessToken, ct).ConfigureAwait(false);

        Report("Получение XSTS токена...");
        var xsts = await AuthorizeXstsAsync(xbl.Token, ct).ConfigureAwait(false);

        var uhs = xsts.UserHash ?? xbl.UserHash
                  ?? throw new AuthException("Xbox не вернул user hash.");

        Report("Вход в Minecraft Services...");
        var mc = await LoginWithXboxAsync(uhs, xsts.Token, ct).ConfigureAwait(false);

        Report("Проверка лицензии Minecraft...");
        await EnsureOwnsMinecraftAsync(mc.AccessToken, ct).ConfigureAwait(false);

        Report("Загрузка профиля...");
        var profile = await GetProfileAsync(mc.AccessToken, ct).ConfigureAwait(false);

        Report($"Добро пожаловать, {profile.Name}!");

        return new MinecraftAccount
        {
            Username = profile.Name,
            Uuid = profile.Id,
            AccessToken = mc.AccessToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(mc.ExpiresIn <= 0 ? 86400 : mc.ExpiresIn),
            MicrosoftRefreshToken = msToken.RefreshToken,
            Xuid = xsts.Xuid ?? xbl.Xuid
        };
    }

    private async Task<XboxAuthResponse> AuthenticateWithXboxLiveAsync(string msAccessToken, CancellationToken ct)
    {
        var payload = new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = "d=" + msAccessToken
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, XblAuthUrl)
        {
            Content = JsonContent.Create(payload)
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new AuthException($"Xbox Live отклонил вход ({(int)resp.StatusCode}): {body}");

        return JsonSerializer.Deserialize<XboxAuthResponse>(body)
               ?? throw new AuthException("Пустой ответ Xbox Live.");
    }

    private async Task<XboxAuthResponse> AuthorizeXstsAsync(string xblToken, CancellationToken ct)
    {
        var payload = new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xblToken }
            },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, XstsAuthUrl)
        {
            Content = JsonContent.Create(payload)
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new AuthException(DescribeXstsError(body));

        if (!resp.IsSuccessStatusCode)
            throw new AuthException($"XSTS ошибка ({(int)resp.StatusCode}): {body}");

        return JsonSerializer.Deserialize<XboxAuthResponse>(body)
               ?? throw new AuthException("Пустой ответ XSTS.");
    }

    private static string DescribeXstsError(string body)
    {
        long xerr = 0;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("XErr", out var x)) xerr = x.GetInt64();
        }
        catch { /* ignore */ }

        return xerr switch
        {
            2148916233 => "У этой учётной записи Microsoft нет профиля Xbox. Создайте его на xbox.com и повторите.",
            2148916235 => "Xbox Live недоступен в стране/регионе вашей учётной записи.",
            2148916236 or 2148916237 => "Требуется подтверждение личности (взрослый аккаунт).",
            2148916238 => "Детская учётная запись должна быть добавлена в семью взрослого.",
            _ => "XSTS отклонил авторизацию. Ответ: " + body
        };
    }

    private async Task<MinecraftLoginResponse> LoginWithXboxAsync(string userHash, string xstsToken, CancellationToken ct)
    {
        var payload = new { identityToken = $"XBL3.0 x={userHash};{xstsToken}" };

        using var req = new HttpRequestMessage(HttpMethod.Post, McLoginUrl)
        {
            Content = JsonContent.Create(payload)
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new AuthException($"Minecraft Services отклонил вход ({(int)resp.StatusCode}): {body}");

        return JsonSerializer.Deserialize<MinecraftLoginResponse>(body)
               ?? throw new AuthException("Пустой ответ Minecraft Services.");
    }

    private async Task EnsureOwnsMinecraftAsync(string mcAccessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, McEntitlementsUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcAccessToken);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return; // не блокируем при 5xx — профиль всё равно проверим

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var ent = JsonSerializer.Deserialize<MinecraftEntitlements>(body);

        if (ent?.Items is null || ent.Items.Count == 0)
            throw new AuthException(
                "На этой учётной записи Microsoft не найдена лицензия Minecraft: Java Edition. " +
                "Игру необходимо приобрести на minecraft.net.");
    }

    private async Task<MinecraftProfileResponse> GetProfileAsync(string mcAccessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, McProfileUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcAccessToken);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new AuthException("Профиль Minecraft не создан. Зайдите на minecraft.net и выберите себе ник.");

        if (!resp.IsSuccessStatusCode)
            throw new AuthException($"Не удалось получить профиль ({(int)resp.StatusCode}): {body}");

        return JsonSerializer.Deserialize<MinecraftProfileResponse>(body)
               ?? throw new AuthException("Пустой профиль.");
    }

    // =====================================================================
    //  OAUTH ВСПОМОГАТЕЛЬНОЕ
    // =====================================================================

    private async Task<MicrosoftTokenResponse> RedeemAuthorizationCodeAsync(
        string code, string codeVerifier, string redirectUri, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
            ["scope"] = Scope
        });

        using var resp = await _http.PostAsync(TokenEndpoint, form, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new AuthException($"Обмен кода на токен не удался ({(int)resp.StatusCode}): {body}");

        return JsonSerializer.Deserialize<MicrosoftTokenResponse>(body)
               ?? throw new AuthException("Пустой ответ токена.");
    }

    private static (HttpListener listener, string redirectUri) StartLoopbackListener()
    {
        // Пробуем набор портов; порт 0 в HttpListener не поддерживается, поэтому подбираем свободный вручную.
        foreach (var port in GetCandidatePorts())
        {
            var prefix = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                return (listener, prefix);
            }
            catch (HttpListenerException)
            {
                try { listener.Close(); } catch { }
            }
        }
        throw new AuthException("Не удалось открыть локальный порт для приёма ответа авторизации.");
    }

    private static IEnumerable<int> GetCandidatePorts()
    {
        // Классические порты лаунчеров + случайные из динамического диапазона
        yield return 30215;
        yield return 30216;
        yield return 30217;
        yield return 8484;
        var rnd = new Random();
        for (int i = 0; i < 20; i++) yield return rnd.Next(29000, 42000);
    }

    private static async Task<string> WaitForAuthorizationCodeAsync(
        HttpListener listener, string expectedState, CancellationToken ct)
    {
        using var reg = ct.Register(() => { try { listener.Stop(); } catch { } });

        while (true)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            catch (HttpListenerException)
            {
                throw new AuthException("Окно авторизации было закрыто.");
            }

            var q = ctx.Request.QueryString;
            var code = q["code"];
            var error = q["error"];
            var state = q["state"];

            // favicon и прочий мусор игнорируем
            if (code is null && error is null)
            {
                await WriteResponseAsync(ctx, 404, "<h1>404</h1>").ConfigureAwait(false);
                continue;
            }

            if (error is not null)
            {
                var desc = q["error_description"] ?? error;
                await WriteResponseAsync(ctx, 400, HtmlPage("Вход не выполнен", desc, false)).ConfigureAwait(false);
                throw new AuthException($"Microsoft вернул ошибку: {desc}");
            }

            if (!string.Equals(state, expectedState, StringComparison.Ordinal))
            {
                await WriteResponseAsync(ctx, 400, HtmlPage("Ошибка безопасности", "Неверный state.", false)).ConfigureAwait(false);
                throw new AuthException("Несовпадение state — возможна попытка подмены ответа.");
            }

            await WriteResponseAsync(ctx, 200,
                HtmlPage("Вход выполнен", "Можно закрыть эту вкладку и вернуться в лаунчер.", true)).ConfigureAwait(false);

            return code!;
        }
    }

    private static async Task WriteResponseAsync(HttpListenerContext ctx, int status, string html)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            ctx.Response.OutputStream.Close();
        }
        catch { /* ignore */ }
    }

    private static string HtmlPage(string title, string message, bool ok) => $$"""
        <!DOCTYPE html>
        <html lang="ru"><head><meta charset="utf-8"><title>{{title}}</title>
        <style>
          body{background:#16181d;color:#e6e8ee;font-family:Segoe UI,Arial,sans-serif;
               display:flex;align-items:center;justify-content:center;height:100vh;margin:0}
          .card{background:#1e2128;border:1px solid #2c3038;border-radius:16px;padding:48px 56px;text-align:center;
                box-shadow:0 20px 60px rgba(0,0,0,.5)}
          h1{margin:0 0 12px;font-size:26px;color:{{(ok ? "#4ade80" : "#f87171")}}}
          p{margin:0;color:#9aa3b2;font-size:15px}
        </style></head>
        <body><div class="card"><h1>{{title}}</h1><p>{{message}}</p></div></body></html>
        """;

    private static (string verifier, string challenge) CreatePkcePair()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        var verifier = Base64Url(bytes);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string RandomString(int len) => Base64Url(RandomNumberGenerator.GetBytes(len))[..len];

    public static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            Process.Start(new ProcessStartInfo("cmd", $"/c start \"\" \"{url}\"") { CreateNoWindow = true });
        }
    }
}

public sealed class AuthException : Exception
{
    public AuthException(string message) : base(message) { }
    public AuthException(string message, Exception inner) : base(message, inner) { }
}
