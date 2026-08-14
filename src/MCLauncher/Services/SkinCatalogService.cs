using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace MCLauncher.Services;

/// <summary>
/// Встроенный каталог скинов (~1000 PNG-текстур, упакованных в ресурсы exe).
/// Работает без интернета; галерея/библиотека дополнительно подгружают
/// сетевые каталоги (minecraftskins.com, MineSkin) — без повторов.
/// </summary>
public static class SkinCatalogService
{
    private const string CatalogResource = "MCLauncher.Assets.skins_catalog.json";
    private const string SkinsPrefix = "MCLauncher.Assets.Skins.";

    private static List<SkinInfo>? _seed;
    private static readonly Dictionary<string, byte[]> BytesCache = new(StringComparer.OrdinalIgnoreCase);

    public static string ResourceName(string file) => SkinsPrefix + file;

    public static byte[]? GetBytes(string file)
    {
        if (string.IsNullOrEmpty(file)) return null;
        lock (BytesCache)
        {
            if (BytesCache.TryGetValue(file, out var cached)) return cached;
        }
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var s = asm.GetManifestResourceStream(ResourceName(file));
            if (s == null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var bytes = ms.ToArray();
            lock (BytesCache)
            {
                BytesCache[file] = bytes;
            }
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Встроенные скины каталога.</summary>
    public static List<SkinInfo> GetSeed()
    {
        if (_seed != null) return _seed;
        var list = new List<SkinInfo>();
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var s = asm.GetManifestResourceStream(CatalogResource);
            if (s == null) return list;
            using var reader = new StreamReader(s);
            var json = reader.ReadToEnd();
            var items = JsonSerializer.Deserialize<List<SeedSkin>>(json);
            if (items == null) return list;
            foreach (var it in items)
            {
                list.Add(new SkinInfo
                {
                    Id = it.Id,
                    Name = it.Name ?? "Скин",
                    Url = it.Url ?? "",
                    PreviewUrl = it.PreviewUrl ?? "",
                    Source = "Catalog",
                    Data = null
                });
            }
        }
        catch { }
        _seed = list;
        return list;
    }

    private class SeedSkin
    {
        public string Id { get; set; } = "";
        public string? Name { get; set; }
        public string? Url { get; set; }
        public string? PreviewUrl { get; set; }
        public string? Source { get; set; }
        public string? File { get; set; }
    }
}