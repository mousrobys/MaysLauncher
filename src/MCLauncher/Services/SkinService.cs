using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MCLauncher.Models;

namespace MCLauncher.Services;

/// <summary>
/// Работа со скинами: чтение текущего скина по UUID и загрузка нового
/// через официальный API api.minecraftservices.com.
/// </summary>
public sealed class SkinService
{
    private const string ProfileUrl = "https://api.minecraftservices.com/minecraft/profile";
    private const string SkinsUrl = "https://api.minecraftservices.com/minecraft/profile/skins";
    private const string ActiveSkinUrl = "https://api.minecraftservices.com/minecraft/profile/skins/active";
    private const string CapeActiveUrl = "https://api.minecraftservices.com/minecraft/profile/capes/active";

    private readonly HttpClient _http;

    public SkinService(HttpClient http) => _http = http;

    // -------------------- Рендеры для UI --------------------

    /// <summary>3D-рендер персонажа (Crafatar).</summary>
    public static string BodyRenderUrl(string uuid, int scale = 10) =>
        $"https://crafatar.com/renders/body/{Clean(uuid)}?scale={scale}&overlay&default=MHF_Steve";

    /// <summary>Голова 3D (Crafatar).</summary>
    public static string HeadRenderUrl(string uuid, int scale = 10) =>
        $"https://crafatar.com/renders/head/{Clean(uuid)}?scale={scale}&overlay&default=MHF_Steve";

    /// <summary>Плоская аватарка.</summary>
    public static string AvatarUrl(string uuid, int size = 64) =>
        $"https://crafatar.com/avatars/{Clean(uuid)}?size={size}&overlay&default=MHF_Steve";

    /// <summary>Сырой файл скина (64x64 png).</summary>
    public static string RawSkinUrl(string uuid) =>
        $"https://crafatar.com/skins/{Clean(uuid)}?default=MHF_Steve";

    /// <summary>Резервный источник рендера, если Crafatar недоступен.</summary>
    public static string FallbackBodyRenderUrl(string username) =>
        $"https://minotar.net/armor/body/{Uri.EscapeDataString(username)}/220.png";

    /// <summary>Аватар по нику (для оффлайн-профилей).</summary>
    public static string AvatarByNameUrl(string username, int size = 64) =>
        $"https://minotar.net/helm/{Uri.EscapeDataString(username)}/{size}.png";

    private static string Clean(string uuid) => uuid.Replace("-", "").Trim();

    // -------------------- Загрузка байтов --------------------

    public async Task<byte[]?> TryDownloadAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Не удалось загрузить изображение {url}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Рендер тела с автоматическим fallback.</summary>
    public async Task<byte[]?> GetBodyRenderAsync(MinecraftAccount acc, CancellationToken ct = default)
    {
        // У оффлайн-профиля UUID локальный, на серверах Mojang его нет —
        // сначала пробуем найти скин по нику, затем откатываемся на Steve.
        if (acc.IsOffline)
        {
            return await TryDownloadAsync(FallbackBodyRenderUrl(acc.Username), ct).ConfigureAwait(false)
                   ?? await TryDownloadAsync(BodyRenderUrl(acc.Uuid), ct).ConfigureAwait(false);
        }

        return await TryDownloadAsync(BodyRenderUrl(acc.Uuid), ct).ConfigureAwait(false)
               ?? await TryDownloadAsync(FallbackBodyRenderUrl(acc.Username), ct).ConfigureAwait(false);
    }

    /// <summary>Аватар с учётом типа аккаунта.</summary>
    public async Task<byte[]?> GetAvatarAsync(MinecraftAccount acc, int size = 72, CancellationToken ct = default)
    {
        if (acc.IsOffline)
        {
            return await TryDownloadAsync(AvatarByNameUrl(acc.Username, size), ct).ConfigureAwait(false)
                   ?? await TryDownloadAsync(AvatarUrl(acc.Uuid, size), ct).ConfigureAwait(false);
        }

        return await TryDownloadAsync(AvatarUrl(acc.Uuid, size), ct).ConfigureAwait(false);
    }

    // -------------------- Профиль --------------------

    public async Task<MinecraftProfileResponse?> GetProfileAsync(string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ProfileUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<MinecraftProfileResponse>(body);
    }

    // -------------------- Смена скина --------------------

    public enum SkinModel { Classic, Slim }

    /// <summary>Загружает файл .png как новый скин (POST multipart на официальный API).</summary>
    public async Task UploadSkinAsync(string accessToken, string filePath, SkinModel model, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Файл скина не найден.", filePath);

        var bytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
        ValidateSkinPng(bytes);

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(model == SkinModel.Slim ? "slim" : "classic"), "variant");

        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(fileContent, "file", Path.GetFileName(filePath));

        using var req = new HttpRequestMessage(HttpMethod.Post, SkinsUrl) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(DescribeSkinError(resp.StatusCode, body));

        Log.Info($"Скин успешно загружен ({model}).");
    }

    /// <summary>Устанавливает скин по прямой ссылке на .png.</summary>
    public async Task ChangeSkinByUrlAsync(string accessToken, string skinUrl, SkinModel model, CancellationToken ct = default)
    {
        var payload = new
        {
            variant = model == SkinModel.Slim ? "slim" : "classic",
            url = skinUrl
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, ActiveSkinUrl)
        {
            Content = JsonContent.Create(payload)
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(DescribeSkinError(resp.StatusCode, body));

        Log.Info("Скин по ссылке успешно применён.");
    }

    /// <summary>Сбрасывает скин на стандартный (Steve/Alex).</summary>
    public async Task ResetSkinAsync(string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, ActiveSkinUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(DescribeSkinError(resp.StatusCode, body));

        Log.Info("Скин сброшен на стандартный.");
    }

    /// <summary>Включает плащ по его id (или снимает, если id == null).</summary>
    public async Task SetCapeAsync(string accessToken, string? capeId, CancellationToken ct = default)
    {
        HttpRequestMessage req;

        if (string.IsNullOrEmpty(capeId))
        {
            req = new HttpRequestMessage(HttpMethod.Delete, CapeActiveUrl);
        }
        else
        {
            req = new HttpRequestMessage(HttpMethod.Put, CapeActiveUrl)
            {
                Content = JsonContent.Create(new { capeId })
            };
        }

        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using (req)
        using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(DescribeSkinError(resp.StatusCode, body));
        }
    }

    // -------------------- Проверки --------------------

    /// <summary>Базовая проверка: PNG сигнатура + размеры 64x64 (или 64x32 для старого формата).</summary>
    public static void ValidateSkinPng(byte[] data)
    {
        if (data.Length < 24)
            throw new InvalidDataException("Файл слишком мал — это не PNG.");

        ReadOnlySpan<byte> sig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (!data.AsSpan(0, 8).SequenceEqual(sig))
            throw new InvalidDataException("Скин должен быть файлом формата PNG.");

        // IHDR: ширина и высота — big-endian с 16-го байта
        var width = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
        var height = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];

        var valid = (width == 64 && (height == 64 || height == 32)) ||
                    (width == 128 && height == 128) ||
                    (width == 256 && height == 256);

        if (!valid)
            throw new InvalidDataException(
                $"Неверный размер скина: {width}x{height}. Требуется 64x64 (или 64x32).");

        if (data.Length > 24 * 1024)
            throw new InvalidDataException("Файл скина слишком большой (максимум 24 КБ).");
    }

    private static string DescribeSkinError(System.Net.HttpStatusCode code, string body)
    {
        var detail = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errorMessage", out var m))
                detail = m.GetString() ?? body;
        }
        catch { }

        return code switch
        {
            System.Net.HttpStatusCode.Unauthorized =>
                "Сессия истекла. Войдите в аккаунт Microsoft заново.",
            System.Net.HttpStatusCode.BadRequest =>
                "Mojang отклонил файл скина: " + detail,
            System.Net.HttpStatusCode.TooManyRequests =>
                "Слишком много запросов к Mojang. Подождите минуту и повторите.",
            _ => $"Ошибка смены скина ({(int)code}): {detail}"
        };
    }
}
