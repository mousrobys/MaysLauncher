using System.Net.Http;
using System.Text.Json;
using MCLauncher.Models;

namespace MCLauncher.Services;

/// <summary>Работа с официальным манифестом версий Mojang.</summary>
public sealed class VersionService
{
    public const string ManifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";
    private const string ManifestMirror = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    /// <summary>Минимальная поддерживаемая версия по ТЗ.</summary>
    public static readonly Version MinimumVersion = new(1, 16, 5);

    private readonly HttpClient _http;

    public VersionService(HttpClient http) => _http = http;

    /// <summary>Хранилище версий (общее либо изолированное для сборки).</summary>
    public GamePaths Paths { get; set; } = GamePaths.Shared;

    public async Task<VersionManifest> GetManifestAsync(CancellationToken ct = default)
    {
        Exception? last = null;

        foreach (var url in new[] { ManifestUrl, ManifestMirror })
        {
            try
            {
                var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
                CacheManifest(json);
                return JsonSerializer.Deserialize<VersionManifest>(json)
                       ?? throw new InvalidOperationException("Пустой манифест.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                Log.Warn($"Манифест недоступен по {url}: {ex.Message}");
            }
        }

        // Оффлайн-режим: пробуем кэш
        var cached = ReadCachedManifest();
        if (cached is not null)
        {
            Log.Warn("Использую кэшированный манифест версий (нет сети).");
            return cached;
        }

        throw new InvalidOperationException(
            "Не удалось загрузить список версий Minecraft. Проверьте подключение к интернету.", last);
    }

    /// <summary>Только релизы 1.16.5 и новее, отсортированные от новых к старым.</summary>
    public static List<ManifestVersion> FilterSupported(VersionManifest manifest, bool includeSnapshots = false)
    {
        return manifest.Versions
            .Where(v => includeSnapshots || v.IsRelease)
            .Where(v => IsAtLeastMinimum(v.Id))
            .OrderByDescending(v => v.ReleaseTime)
            .ToList();
    }

    /// <summary>Сравнение версий вида 1.20.4 / 1.17 / 24w14a.</summary>
    public static bool IsAtLeastMinimum(string id)
    {
        var parsed = ParseMcVersion(id);
        if (parsed is null)
        {
            // Снапшоты формата 21w03a и новее (год >= 21) считаем свежее 1.16.5
            if (id.Length >= 2 && char.IsDigit(id[0]) && char.IsDigit(id[1]) && id.Contains('w'))
                return int.TryParse(id[..2], out var year) && year >= 21;
            return false;
        }
        return parsed >= MinimumVersion;
    }

    public static Version? ParseMcVersion(string id)
    {
        var core = id.Split('-')[0].Trim();
        var parts = core.Split('.');
        if (parts.Length < 2) return null;

        if (!int.TryParse(parts[0], out var major)) return null;
        if (!int.TryParse(parts[1], out var minor)) return null;
        var build = 0;
        if (parts.Length >= 3 && !int.TryParse(parts[2], out build)) return null;

        return new Version(major, minor, build);
    }

    /// <summary>Скачивает (или берёт с диска) JSON конкретной версии.</summary>
    public async Task<VersionDetail> GetVersionDetailAsync(ManifestVersion version, CancellationToken ct = default)
    {
        var path = Paths.VersionJson(version.Id);

        if (File.Exists(path))
        {
            try
            {
                var cachedJson = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                var cached = JsonSerializer.Deserialize<VersionDetail>(cachedJson);
                if (cached is not null && !string.IsNullOrEmpty(cached.MainClass))
                    return cached;
            }
            catch (Exception ex)
            {
                Log.Warn($"Повреждённый {version.Id}.json, перекачиваю: {ex.Message}");
            }
        }

        Log.Info($"Скачиваю описание версии {version.Id}...");
        var json = await _http.GetStringAsync(version.Url, ct).ConfigureAwait(false);

        Directory.CreateDirectory(Paths.VersionDir(version.Id));
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);

        return JsonSerializer.Deserialize<VersionDetail>(json)
               ?? throw new InvalidOperationException($"Не удалось разобрать JSON версии {version.Id}.");
    }

    /// <summary>
    /// Загружает версию с диска по id (для профилей Fabric/Forge, которых нет в манифесте Mojang).
    /// </summary>
    public async Task<VersionDetail?> LoadLocalVersionAsync(string versionId, CancellationToken ct = default)
    {
        var path = Paths.VersionJson(versionId);
        if (!File.Exists(path)) return null;

        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<VersionDetail>(json);
    }

    /// <summary>
    /// Разрешает версию по id: сначала локальный профиль (модлоадер), иначе манифест.
    /// Затем рекурсивно применяет inheritsFrom.
    /// </summary>
    public async Task<VersionDetail> ResolveAsync(string versionId, CancellationToken ct = default)
    {
        var local = await LoadLocalVersionAsync(versionId, ct).ConfigureAwait(false);

        if (local is null)
        {
            var manifest = await GetManifestAsync(ct).ConfigureAwait(false);
            var mv = manifest.Versions.FirstOrDefault(v => v.Id == versionId)
                     ?? throw new InvalidOperationException($"Версия {versionId} не найдена ни локально, ни в манифесте.");
            local = await GetVersionDetailAsync(mv, ct).ConfigureAwait(false);
        }

        return await ResolveInheritanceAsync(local, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Профили Fabric/Forge/NeoForge содержат только свои дополнения и ссылку inheritsFrom
    /// на ванильную версию. Здесь мы сливаем их в один полный VersionDetail.
    /// </summary>
    public async Task<VersionDetail> ResolveInheritanceAsync(
        VersionDetail child, CancellationToken ct = default, int depth = 0)
    {
        if (string.IsNullOrEmpty(child.InheritsFrom)) return child;

        if (depth > 8)
            throw new InvalidOperationException("Слишком глубокая цепочка inheritsFrom — возможна циклическая ссылка.");

        var parentId = child.InheritsFrom!;
        Log.Info($"Версия {child.Id} наследует {parentId} — объединяю профили.");

        var parentLocal = await LoadLocalVersionAsync(parentId, ct).ConfigureAwait(false);

        if (parentLocal is null)
        {
            var manifest = await GetManifestAsync(ct).ConfigureAwait(false);
            var mv = manifest.Versions.FirstOrDefault(v => v.Id == parentId)
                     ?? throw new InvalidOperationException(
                         $"Базовая версия {parentId} не найдена (нужна для {child.Id}).");
            parentLocal = await GetVersionDetailAsync(mv, ct).ConfigureAwait(false);
        }

        var parent = await ResolveInheritanceAsync(parentLocal, ct, depth + 1).ConfigureAwait(false);

        return Merge(parent, child);
    }

    /// <summary>Слияние родительского и дочернего профиля по правилам официального лаунчера.</summary>
    private static VersionDetail Merge(VersionDetail parent, VersionDetail child)
    {
        var merged = new VersionDetail
        {
            // id и mainClass берём у ребёнка
            Id = child.Id,
            InheritsFrom = null,
            MainClass = string.IsNullOrEmpty(child.MainClass) ? parent.MainClass : child.MainClass,
            Type = string.IsNullOrEmpty(child.Type) ? parent.Type : child.Type,

            // Ресурсы игры — от родителя (у модлоадера их нет)
            Assets = string.IsNullOrEmpty(child.Assets) || child.Assets == "legacy" ? parent.Assets : child.Assets,
            AssetIndex = child.AssetIndex ?? parent.AssetIndex,
            Downloads = child.Downloads ?? parent.Downloads,
            JavaVersion = child.JavaVersion ?? parent.JavaVersion,
            Logging = child.Logging ?? parent.Logging,

            // minecraftArguments (старый формат) — ребёнок важнее
            MinecraftArguments = child.MinecraftArguments ?? parent.MinecraftArguments,

            // Библиотеки: сначала дочерние (приоритет), затем родительские без дублей
            Libraries = MergeLibraries(parent.Libraries, child.Libraries),

            // arguments объединяем поэлементно
            Arguments = MergeArguments(parent.Arguments, child.Arguments)
        };

        return merged;
    }

    private static List<Library> MergeLibraries(List<Library> parent, List<Library> child)
    {
        var result = new List<Library>(child);

        // Ключ = group:artifact[:classifier] без версии — чтобы версия модлоадера победила
        var taken = child.Select(LibraryKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var lib in parent)
        {
            if (taken.Add(LibraryKey(lib))) result.Add(lib);
        }

        return result;
    }

    private static string LibraryKey(Library lib)
    {
        var parts = lib.Name.Split(':');
        if (parts.Length < 2) return lib.Name;
        var classifier = parts.Length >= 4 ? ":" + parts[3] : "";
        return parts[0] + ":" + parts[1] + classifier;
    }

    private static JsonElement? MergeArguments(JsonElement? parent, JsonElement? child)
    {
        if (parent is null) return child;
        if (child is null) return parent;

        var p = parent.Value;
        var c = child.Value;
        if (p.ValueKind != JsonValueKind.Object || c.ValueKind != JsonValueKind.Object) return child;

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();

            foreach (var section in new[] { "game", "jvm" })
            {
                var hasP = p.TryGetProperty(section, out var pv) && pv.ValueKind == JsonValueKind.Array;
                var hasC = c.TryGetProperty(section, out var cv) && cv.ValueKind == JsonValueKind.Array;
                if (!hasP && !hasC) continue;

                w.WritePropertyName(section);
                w.WriteStartArray();

                // Родительские аргументы идут первыми, дочерние дополняют
                if (hasP) foreach (var e in pv.EnumerateArray()) e.WriteTo(w);
                if (hasC) foreach (var e in cv.EnumerateArray()) e.WriteTo(w);

                w.WriteEndArray();
            }

            w.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    private static string CachePath => Path.Combine(LauncherPaths.CacheDir, "version_manifest_v2.json");

    private static void CacheManifest(string json)
    {
        try
        {
            Directory.CreateDirectory(LauncherPaths.CacheDir);
            File.WriteAllText(CachePath, json);
        }
        catch { /* ignore */ }
    }

    private static VersionManifest? ReadCachedManifest()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            return JsonSerializer.Deserialize<VersionManifest>(File.ReadAllText(CachePath));
        }
        catch { return null; }
    }
}
