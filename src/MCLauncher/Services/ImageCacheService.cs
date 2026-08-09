using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace MCLauncher.Services;

/// <summary>
/// Асинхронная загрузка картинок из сети.
///
/// Важно: нельзя отдавать BitmapImage с UriSource = http(s), потому что при
/// CacheOption.OnLoad WPF качает файл синхронно прямо в UI-потоке. На медленном
/// соединении это выглядит как «программа не отвечает» и Windows предлагает её закрыть.
/// Поэтому байты тянем через HttpClient, а BitmapImage строим уже из памяти.
/// </summary>
public static class ImageCacheService
{
    private static readonly ConcurrentDictionary<string, BitmapImage> Memory = new();
    private static readonly SemaphoreSlim Limiter = new(6);

    private static string DiskDir => Path.Combine(LauncherPaths.CacheDir, "images");

    /// <summary>Отдаёт картинку из памяти, если она уже загружена.</summary>
    public static BitmapImage? TryGetCached(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        return Memory.TryGetValue(url, out var img) ? img : null;
    }

    /// <summary>
    /// Загружает картинку: память → диск → сеть. Всегда возвращает
    /// замороженный (Freeze) объект, безопасный для UI-потока.
    /// </summary>
    public static async Task<BitmapImage?> GetAsync(
        string? url, HttpClient http, int decodeWidth = 128, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (Memory.TryGetValue(url!, out var cached)) return cached;

        try
        {
            var path = DiskPathFor(url!);
            byte[]? bytes = null;

            if (File.Exists(path))
            {
                try { bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false); }
                catch { bytes = null; }
            }

            if (bytes is null || bytes.Length == 0)
            {
                await Limiter.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("User-Agent", "MaysLauncher/1.0");

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(15));

                    using var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) return null;

                    bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                }
                finally { Limiter.Release(); }

                if (bytes.Length == 0) return null;

                try
                {
                    Directory.CreateDirectory(DiskDir);
                    await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
                }
                catch { /* кэш на диске необязателен */ }
            }

            return Decode(url!, bytes, decodeWidth);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось загрузить изображение {url}: {ex.Message}");
            return null;
        }
    }

    private static BitmapImage? Decode(string url, byte[] bytes, int decodeWidth)
    {
        try
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);

            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();

            Memory[url] = bmp;
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Warn($"Битое изображение {url}: {ex.Message}");
            return null;
        }
    }

    private static string DiskPathFor(string url)
    {
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        var ext = Path.GetExtension(new Uri(url).AbsolutePath);
        if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".img";
        return Path.Combine(DiskDir, hash + ext);
    }

    public static void ClearMemory() => Memory.Clear();

    public static long DiskCacheSize()
    {
        try
        {
            if (!Directory.Exists(DiskDir)) return 0;
            return new DirectoryInfo(DiskDir).EnumerateFiles().Sum(f => f.Length);
        }
        catch { return 0; }
    }
}
