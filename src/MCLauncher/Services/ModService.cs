using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using MCLauncher.Models;

namespace MCLauncher.Services;

/// <summary>
/// Поиск и установка модов из Modrinth и CurseForge.
///
/// Modrinth работает без ключа. CurseForge требует x-api-key: ключ выдаётся
/// в console.curseforge.com, причём часть эндпоинтов (mods/search, games/{id})
/// может быть закрыта, пока приложение не одобрено — в этом случае лаунчер
/// молча продолжает работать только с Modrinth.
/// </summary>
public sealed class ModService
{
    // ---------- Modrinth ----------
    private const string ModrinthBase = "https://api.modrinth.com/v2";

    // ---------- CurseForge ----------
    private const string CurseBase = "https://api.curseforge.com/v1";
    private const int CurseMinecraftGameId = 432;

    // classId в CurseForge
    private const int CurseClassMods = 6;
    private const int CurseClassResourcePacks = 12;
    private const int CurseClassShaders = 6552;
    private const int CurseClassModpacks = 4471;

    /// <summary>Ключ CurseForge API (заголовок x-api-key).</summary>
    public const string CurseForgeApiKey =
        "$2a$10$bLg9FB9sTvausnTAWSbM8uGFvXYfvtcr05CoUI67UKV6768wRI7G2";

    private readonly HttpClient _http;

    /// <summary>Доступен ли поиск CurseForge (проверяется при первом обращении).</summary>
    public bool CurseForgeAvailable { get; private set; } = true;
    public string? CurseForgeError { get; private set; }

    public ModService(HttpClient http) => _http = http;

    public event Action<string>? Status;
    private void Report(string s) { Status?.Invoke(s); Log.Info(s); }

    // =====================================================================
    //  ЗАПРОСЫ К CURSEFORGE (с заголовком x-api-key)
    // =====================================================================

    /// <summary>
    /// Базовый GET-запрос к CurseForge: подставляет x-api-key и Accept,
    /// корректно разбирая 403 (нет доступа к эндпоинту) и 429 (лимит).
    /// </summary>
    private async Task<JsonDocument?> CurseGetAsync(string relativeUrl, CancellationToken ct)
    {
        var url = relativeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? relativeUrl
            : CurseBase + relativeUrl;

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("x-api-key", CurseForgeApiKey);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            CurseForgeAvailable = false;
            CurseForgeError = $"CurseForge отклонил запрос ({(int)resp.StatusCode}). " +
                              "Ключ не имеет доступа к этому эндпоинту — используется только Modrinth.";
            Log.Warn(CurseForgeError + "  URL: " + url);
            return null;
        }

        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
        {
            CurseForgeError = "CurseForge: превышен лимит запросов, подождите минуту.";
            Log.Warn(CurseForgeError);
            return null;
        }

        if (!resp.IsSuccessStatusCode)
        {
            Log.Warn($"CurseForge вернул {(int)resp.StatusCode} на {url}");
            return null;
        }

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(json);
    }

    /// <summary>Проверка доступности CurseForge API с текущим ключом.</summary>
    public async Task<bool> CheckCurseForgeAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = await CurseGetAsync($"/mods/search?gameId={CurseMinecraftGameId}&pageSize=1", ct)
                .ConfigureAwait(false);

            CurseForgeAvailable = doc is not null;
            if (CurseForgeAvailable) CurseForgeError = null;
            return CurseForgeAvailable;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CurseForgeAvailable = false;
            CurseForgeError = "CurseForge недоступен: " + ex.Message;
            return false;
        }
    }

    // =====================================================================
    //  ПОИСК
    // =====================================================================

    /// <summary>Страница результатов поиска.</summary>
    public sealed class SearchPage
    {
        public required List<ModSearchResult> Items { get; init; }
        public required int TotalCount { get; init; }
        public required int Offset { get; init; }
        public required int PageSize { get; init; }

        public int PageNumber => PageSize > 0 ? Offset / PageSize + 1 : 1;
        public int TotalPages => PageSize > 0 ? Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize)) : 1;
        public bool HasPrevious => Offset > 0;
        public bool HasNext => Offset + PageSize < TotalCount;
    }

    public async Task<SearchPage> SearchAsync(
        string query, string mcVersion, LoaderKind loader,
        ModContentType type = ModContentType.Mod,
        ModProvider? onlyProvider = null,
        int limit = 20, int offset = 0, CancellationToken ct = default)
    {
        var results = new List<ModSearchResult>();
        var total = 0;

        var wantModrinth = onlyProvider is null or ModProvider.Modrinth;
        var wantCurse = (onlyProvider is null or ModProvider.CurseForge) && CurseForgeAvailable;

        // Когда источников два, делим страницу пополам, чтобы сохранить размер страницы
        var perSource = (wantModrinth && wantCurse) ? Math.Max(1, limit / 2) : limit;
        var srcOffset = (wantModrinth && wantCurse) ? offset / 2 : offset;

        if (wantModrinth)
        {
            var (items, cnt) = await SearchModrinthAsync(query, mcVersion, loader, type, perSource, srcOffset, ct)
                .ConfigureAwait(false);
            results.AddRange(items);
            total += cnt;
        }

        if (wantCurse)
        {
            var (items, cnt) = await SearchCurseForgeAsync(query, mcVersion, loader, type, perSource, srcOffset, ct)
                .ConfigureAwait(false);
            results.AddRange(items);
            total += cnt;
        }

        return new SearchPage
        {
            Items = results.OrderByDescending(r => r.Downloads).ToList(),
            TotalCount = total,
            Offset = offset,
            PageSize = limit
        };
    }

    // ---------------- Modrinth ----------------

    private async Task<(List<ModSearchResult> Items, int Total)> SearchModrinthAsync(
        string query, string mcVersion, LoaderKind loader,
        ModContentType type, int limit, int offset, CancellationToken ct)
    {
        var list = new List<ModSearchResult>();
        var total = 0;

        try
        {
            var facets = new List<string>
            {
                $"[\"project_type:{ModrinthProjectType(type)}\"]"
            };

            if (!string.IsNullOrEmpty(mcVersion))
                facets.Add($"[\"versions:{mcVersion}\"]");

            // Для ресурспаков/шейдеров фильтр по загрузчику не применяется
            if (type == ModContentType.Mod && loader != LoaderKind.Vanilla)
                facets.Add($"[\"categories:{ModrinthLoader(loader)}\"]");

            var url = $"{ModrinthBase}/search" +
                      $"?query={Uri.EscapeDataString(query ?? "")}" +
                      $"&limit={limit}" +
                      $"&offset={offset}" +
                      $"&index=downloads" +
                      $"&facets=[{string.Join(",", facets)}]";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "MaysLauncher/1.0 (Minecraft launcher)");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"Modrinth вернул {(int)resp.StatusCode}");
                return (list, 0);
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("total_hits", out var th) && th.ValueKind == JsonValueKind.Number)
                total = th.GetInt32();

            if (!doc.RootElement.TryGetProperty("hits", out var hits)) return (list, total);

            foreach (var h in hits.EnumerateArray())
            {
                var id = Str(h, "project_id");
                if (id is null) continue;

                list.Add(new ModSearchResult
                {
                    Provider = ModProvider.Modrinth,
                    ProjectId = id,
                    Title = Str(h, "title") ?? id,
                    Slug = Str(h, "slug") ?? "",
                    Summary = Str(h, "description") ?? "",
                    Author = Str(h, "author") ?? "",
                    IconUrl = Str(h, "icon_url"),
                    Downloads = Num(h, "downloads"),
                    Updated = Date(h, "date_modified"),
                    Categories = StrList(h, "categories"),
                    Loaders = StrList(h, "categories")
                        .Where(c => c is "fabric" or "forge" or "neoforge" or "quilt").ToList(),
                    ContentType = type,
                    PageUrl = $"https://modrinth.com/{ModrinthProjectType(type)}/{Str(h, "slug")}"
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("Ошибка поиска Modrinth: " + ex.Message);
        }

        return (list, total);
    }

    // ---------------- CurseForge ----------------

    private async Task<(List<ModSearchResult> Items, int Total)> SearchCurseForgeAsync(
        string query, string mcVersion, LoaderKind loader,
        ModContentType type, int limit, int offset, CancellationToken ct)
    {
        var list = new List<ModSearchResult>();
        var total = 0;

        try
        {
            var url = $"/mods/search?gameId={CurseMinecraftGameId}" +
                      $"&classId={CurseClassId(type)}" +
                      $"&pageSize={Math.Clamp(limit, 1, 50)}" +
                      $"&index={offset}" +
                      $"&sortField=6&sortOrder=desc";

            if (!string.IsNullOrWhiteSpace(query))
                url += $"&searchFilter={Uri.EscapeDataString(query)}";

            if (!string.IsNullOrEmpty(mcVersion))
                url += $"&gameVersion={Uri.EscapeDataString(mcVersion)}";

            if (type == ModContentType.Mod && loader != LoaderKind.Vanilla)
                url += $"&modLoaderType={CurseLoaderType(loader)}";

            using var doc = await CurseGetAsync(url, ct).ConfigureAwait(false);
            if (doc is null) return (list, 0);

            if (doc.RootElement.TryGetProperty("pagination", out var pg) &&
                pg.TryGetProperty("totalCount", out var tc) && tc.ValueKind == JsonValueKind.Number)
                total = tc.GetInt32();

            if (!doc.RootElement.TryGetProperty("data", out var data)) return (list, total);

            foreach (var m in data.EnumerateArray())
            {
                var id = m.TryGetProperty("id", out var idEl) ? idEl.GetInt64().ToString() : null;
                if (id is null) continue;

                string? icon = null;
                if (m.TryGetProperty("logo", out var logo) && logo.ValueKind == JsonValueKind.Object)
                    icon = Str(logo, "thumbnailUrl") ?? Str(logo, "url");

                var author = "";
                if (m.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array)
                    author = authors.EnumerateArray().Select(a => Str(a, "name")).FirstOrDefault(s => s is not null) ?? "";

                var cats = new List<string>();
                if (m.TryGetProperty("categories", out var catEl) && catEl.ValueKind == JsonValueKind.Array)
                    cats = catEl.EnumerateArray().Select(c => Str(c, "name") ?? "").Where(s => s.Length > 0).ToList();

                string? page = null;
                if (m.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Object)
                    page = Str(links, "websiteUrl");

                list.Add(new ModSearchResult
                {
                    Provider = ModProvider.CurseForge,
                    ProjectId = id,
                    Title = Str(m, "name") ?? id,
                    Slug = Str(m, "slug") ?? "",
                    Summary = Str(m, "summary") ?? "",
                    Author = author,
                    IconUrl = icon,
                    Downloads = (long)(m.TryGetProperty("downloadCount", out var dc) ? dc.GetDouble() : 0),
                    Updated = Date(m, "dateModified"),
                    Categories = cats,
                    ContentType = type,
                    PageUrl = page
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("Ошибка поиска CurseForge: " + ex.Message);
        }

        return (list, total);
    }

    // =====================================================================
    //  ФАЙЛЫ ПРОЕКТА
    // =====================================================================

    /// <summary>Все версии проекта без фильтров — для диалога выбора версии.</summary>
    public async Task<List<ModFile>> GetAllFilesAsync(ModSearchResult project, CancellationToken ct = default)
    {
        return project.Provider == ModProvider.Modrinth
            ? await GetModrinthFilesAsync(project.ProjectId, "", LoaderKind.Vanilla, ct).ConfigureAwait(false)
            : await GetCurseFilesAsync(project.ProjectId, "", LoaderKind.Vanilla, ct).ConfigureAwait(false);
    }
    public async Task<List<ModFile>> GetFilesAsync(
        ModSearchResult project, string mcVersion, LoaderKind loader, CancellationToken ct = default)
    {
        return project.Provider == ModProvider.Modrinth
            ? await GetModrinthFilesAsync(project.ProjectId, mcVersion, loader, ct).ConfigureAwait(false)
            : await GetCurseFilesAsync(project.ProjectId, mcVersion, loader, ct).ConfigureAwait(false);
    }

    private async Task<List<ModFile>> GetModrinthFilesAsync(
        string projectId, string mcVersion, LoaderKind loader, CancellationToken ct)
    {
        var list = new List<ModFile>();

        var url = $"{ModrinthBase}/project/{Uri.EscapeDataString(projectId)}/version";
        var qs = new List<string>();
        if (!string.IsNullOrEmpty(mcVersion)) qs.Add($"game_versions=[\"{mcVersion}\"]");
        if (loader != LoaderKind.Vanilla) qs.Add($"loaders=[\"{ModrinthLoader(loader)}\"]");
        if (qs.Count > 0) url += "?" + string.Join("&", qs);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "MaysLauncher/1.0");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return list;

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        foreach (var v in doc.RootElement.EnumerateArray())
        {
            if (!v.TryGetProperty("files", out var files)) continue;

            var primary = files.EnumerateArray()
                .FirstOrDefault(f => f.TryGetProperty("primary", out var p) && p.GetBoolean());
            if (primary.ValueKind != JsonValueKind.Object)
                primary = files.EnumerateArray().FirstOrDefault();
            if (primary.ValueKind != JsonValueKind.Object) continue;

            string? sha1 = null;
            if (primary.TryGetProperty("hashes", out var hashes)) sha1 = Str(hashes, "sha1");

            var deps = new List<ModDependency>();
            if (v.TryGetProperty("dependencies", out var depEl) && depEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in depEl.EnumerateArray())
                {
                    deps.Add(new ModDependency
                    {
                        ProjectId = Str(d, "project_id"),
                        FileId = Str(d, "version_id"),
                        Type = Str(d, "dependency_type") ?? "required"
                    });
                }
            }

            list.Add(new ModFile
            {
                Provider = ModProvider.Modrinth,
                ProjectId = projectId,
                FileId = Str(v, "id") ?? "",
                FileName = Str(primary, "filename") ?? "mod.jar",
                DownloadUrl = Str(primary, "url") ?? "",
                DisplayName = Str(v, "name") ?? Str(v, "version_number") ?? "",
                Size = primary.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
                Sha1 = sha1,
                Published = Date(v, "date_published"),
                GameVersions = StrList(v, "game_versions"),
                Loaders = StrList(v, "loaders"),
                ReleaseType = Str(v, "version_type") ?? "release",
                Dependencies = deps
            });
        }

        return list.OrderByDescending(f => f.Published ?? DateTimeOffset.MinValue).ToList();
    }

    private async Task<List<ModFile>> GetCurseFilesAsync(
        string projectId, string mcVersion, LoaderKind loader, CancellationToken ct)
    {
        var list = new List<ModFile>();

        var url = $"/mods/{projectId}/files?pageSize=50";
        if (!string.IsNullOrEmpty(mcVersion)) url += $"&gameVersion={Uri.EscapeDataString(mcVersion)}";
        if (loader != LoaderKind.Vanilla) url += $"&modLoaderType={CurseLoaderType(loader)}";

        using var doc = await CurseGetAsync(url, ct).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var data)) return list;

        foreach (var f in data.EnumerateArray())
        {
            var fileId = f.TryGetProperty("id", out var fid) ? fid.GetInt64().ToString() : null;
            if (fileId is null) continue;

            var downloadUrl = Str(f, "downloadUrl");

            // CurseForge иногда отдаёт null для downloadUrl — собираем ссылку вручную
            if (string.IsNullOrEmpty(downloadUrl))
            {
                var idNum = long.Parse(fileId);
                var name = Str(f, "fileName") ?? "";
                downloadUrl = $"https://edge.forgecdn.net/files/{idNum / 1000}/{idNum % 1000}/{name}";
            }

            string? sha1 = null;
            if (f.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in hashes.EnumerateArray())
                {
                    // algo: 1 = SHA1, 2 = MD5
                    if (h.TryGetProperty("algo", out var algo) && algo.GetInt32() == 1)
                        sha1 = Str(h, "value");
                }
            }

            var deps = new List<ModDependency>();
            if (f.TryGetProperty("dependencies", out var depEl) && depEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in depEl.EnumerateArray())
                {
                    // relationType: 3 = RequiredDependency, 2 = OptionalDependency
                    var rel = d.TryGetProperty("relationType", out var rt) ? rt.GetInt32() : 0;
                    if (rel is not (2 or 3)) continue;

                    deps.Add(new ModDependency
                    {
                        ProjectId = d.TryGetProperty("modId", out var mid) ? mid.GetInt64().ToString() : null,
                        Type = rel == 3 ? "required" : "optional"
                    });
                }
            }

            var relType = f.TryGetProperty("releaseType", out var rtp) ? rtp.GetInt32() : 1;

            list.Add(new ModFile
            {
                Provider = ModProvider.CurseForge,
                ProjectId = projectId,
                FileId = fileId,
                FileName = Str(f, "fileName") ?? "mod.jar",
                DownloadUrl = downloadUrl!,
                DisplayName = Str(f, "displayName") ?? "",
                Size = f.TryGetProperty("fileLength", out var fl) ? fl.GetInt64() : 0,
                Sha1 = sha1,
                Published = Date(f, "fileDate"),
                GameVersions = StrList(f, "gameVersions"),
                ReleaseType = relType switch { 1 => "release", 2 => "beta", _ => "alpha" },
                Dependencies = deps
            });
        }

        return list.OrderByDescending(f => f.Published ?? DateTimeOffset.MinValue).ToList();
    }

    /// <summary>Получить один проект по id (нужно для зависимостей).</summary>
    public async Task<ModSearchResult?> GetProjectAsync(
        ModProvider provider, string projectId, CancellationToken ct = default)
    {
        try
        {
            if (provider == ModProvider.Modrinth)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"{ModrinthBase}/project/{Uri.EscapeDataString(projectId)}");
                req.Headers.TryAddWithoutValidation("User-Agent", "MaysLauncher/1.0");

                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;

                using var doc = JsonDocument.Parse(
                    await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                var r = doc.RootElement;

                return new ModSearchResult
                {
                    Provider = ModProvider.Modrinth,
                    ProjectId = Str(r, "id") ?? projectId,
                    Title = Str(r, "title") ?? projectId,
                    Slug = Str(r, "slug") ?? "",
                    Summary = Str(r, "description") ?? "",
                    IconUrl = Str(r, "icon_url"),
                    Downloads = Num(r, "downloads")
                };
            }

            using var cdoc = await CurseGetAsync($"/mods/{projectId}", ct).ConfigureAwait(false);
            if (cdoc is null || !cdoc.RootElement.TryGetProperty("data", out var d)) return null;

            string? icon = null;
            if (d.TryGetProperty("logo", out var logo)) icon = Str(logo, "thumbnailUrl");

            return new ModSearchResult
            {
                Provider = ModProvider.CurseForge,
                ProjectId = projectId,
                Title = Str(d, "name") ?? projectId,
                Summary = Str(d, "summary") ?? "",
                IconUrl = icon,
                Downloads = (long)(d.TryGetProperty("downloadCount", out var dc) ? dc.GetDouble() : 0)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Не удалось получить проект {projectId}: {ex.Message}");
            return null;
        }
    }

    // =====================================================================
    //  УСТАНОВКА
    // =====================================================================

    public sealed class InstallOutcome
    {
        public required List<InstalledMod> Installed { get; init; }
        public required List<string> Skipped { get; init; }
        public required List<string> Failed { get; init; }
    }

    /// <summary>
    /// Скачивает мод в папку сборки и, при необходимости, тянет обязательные зависимости.
    /// </summary>
    public async Task<InstallOutcome> InstallAsync(
        ModFile file, string targetDir, string mcVersion, LoaderKind loader,
        bool withDependencies = true, CancellationToken ct = default)
    {
        var installed = new List<InstalledMod>();
        var skipped = new List<string>();
        var failed = new List<string>();

        Directory.CreateDirectory(targetDir);

        await InstallOneAsync(file, targetDir, installed, failed, skipped, false, ct).ConfigureAwait(false);

        if (withDependencies)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { file.ProjectId };

            foreach (var dep in file.Dependencies.Where(d => d.IsRequired && d.ProjectId is not null))
            {
                ct.ThrowIfCancellationRequested();
                if (!seen.Add(dep.ProjectId!)) continue;

                try
                {
                    var depProject = new ModSearchResult
                    {
                        Provider = file.Provider,
                        ProjectId = dep.ProjectId!,
                        Title = dep.ProjectId!
                    };

                    var depFiles = await GetFilesAsync(depProject, mcVersion, loader, ct).ConfigureAwait(false);
                    var best = PickBest(depFiles);

                    if (best is null)
                    {
                        skipped.Add($"зависимость {dep.ProjectId} (нет версии для {mcVersion})");
                        continue;
                    }

                    Report($"Ставлю зависимость: {best.FileName}");
                    await InstallOneAsync(best, targetDir, installed, failed, skipped, true, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed.Add($"зависимость {dep.ProjectId}: {ex.Message}");
                }
            }
        }

        return new InstallOutcome { Installed = installed, Skipped = skipped, Failed = failed };
    }

    private async Task InstallOneAsync(
        ModFile file, string targetDir,
        List<InstalledMod> installed, List<string> failed, List<string> skipped,
        bool asDependency, CancellationToken ct)
    {
        var path = Path.Combine(targetDir, SanitizeFileName(file.FileName));

        if (File.Exists(path))
        {
            skipped.Add(file.FileName + " (уже установлен)");
            return;
        }

        if (string.IsNullOrEmpty(file.DownloadUrl))
        {
            failed.Add(file.FileName + ": нет ссылки на скачивание");
            return;
        }

        try
        {
            Report($"Скачиваю {file.FileName}...");

            var tmp = path + ".part";

            using (var req = new HttpRequestMessage(HttpMethod.Get, file.DownloadUrl))
            {
                req.Headers.TryAddWithoutValidation("User-Agent", "MaysLauncher/1.0");
                if (file.Provider == ModProvider.CurseForge)
                    req.Headers.TryAddWithoutValidation("x-api-key", CurseForgeApiKey);

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                    81920, true);
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(file.Sha1))
            {
                var actual = await ComputeSha1Async(tmp, ct).ConfigureAwait(false);
                if (!string.Equals(actual, file.Sha1, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tmp);
                    failed.Add(file.FileName + ": контрольная сумма не совпала");
                    return;
                }
            }

            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);

            installed.Add(new InstalledMod
            {
                Provider = file.Provider,
                ProjectId = file.ProjectId,
                FileId = file.FileId,
                Title = string.IsNullOrEmpty(file.DisplayName) ? file.FileName : file.DisplayName,
                FileName = Path.GetFileName(path),
                Version = file.DisplayName,
                Size = new FileInfo(path).Length,
                InstalledAsDependency = asDependency
            });

            Report($"Установлен: {Path.GetFileName(path)}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            failed.Add(file.FileName + ": " + ex.Message);
        }
    }

    /// <summary>Выбирает лучший файл: свежий release, иначе просто свежий.</summary>
    public static ModFile? PickBest(List<ModFile> files)
    {
        if (files.Count == 0) return null;

        return files.FirstOrDefault(f =>
                   string.Equals(f.ReleaseType, "release", StringComparison.OrdinalIgnoreCase))
               ?? files[0];
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    private static async Task<string> ComputeSha1Async(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var sha = SHA1.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false)).ToLowerInvariant();
    }

    // =====================================================================
    //  ПРОВЕРКА ОБНОВЛЕНИЙ
    // =====================================================================

    public sealed class ModUpdate
    {
        public required string FilePath { get; init; }
        public required string CurrentName { get; init; }
        public required ModSearchResult Project { get; init; }
        public required ModFile NewFile { get; init; }
        public string CurrentVersion { get; init; } = "";

        public string NewVersion => string.IsNullOrWhiteSpace(NewFile.DisplayName)
            ? NewFile.FileName : NewFile.DisplayName;
    }

    /// <summary>
    /// Ищет обновления по SHA1: Modrinth умеет отдавать проект по хэшу файла,
    /// поэтому опознаём даже моды, скачанные не через лаунчер.
    /// </summary>
    public async Task<List<ModUpdate>> CheckUpdatesAsync(
        string modsDir, string mcVersion, LoaderKind loader,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var updates = new List<ModUpdate>();

        if (!Directory.Exists(modsDir)) return updates;

        var files = Directory.GetFiles(modsDir, "*.jar");
        if (files.Length == 0) return updates;

        // 1. Считаем хэши
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            try { hashes[await ComputeSha1Async(f, ct).ConfigureAwait(false)] = f; }
            catch (Exception ex) { Log.Warn($"Хэш {Path.GetFileName(f)}: {ex.Message}"); }
        }

        if (hashes.Count == 0) return updates;

        progress?.Report($"Опознаю {hashes.Count} модов на Modrinth...");

        // 2. Пакетный запрос: хэш -> версия проекта
        Dictionary<string, JsonElement> known;
        try
        {
            var payload = new
            {
                hashes = hashes.Keys.ToArray(),
                algorithm = "sha1"
            };

            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{ModrinthBase}/version_files")
            {
                Content = JsonContent.Create(payload)
            };
            req.Headers.TryAddWithoutValidation("User-Agent", "MaysLauncher/1.0");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"Modrinth version_files вернул {(int)resp.StatusCode}");
                return updates;
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            known = doc.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("Проверка обновлений: " + ex.Message);
            return updates;
        }

        progress?.Report($"Опознано {known.Count} из {hashes.Count}. Ищу свежие версии...");

        // 3. Для каждого опознанного мода смотрим, есть ли версия новее
        foreach (var (hash, versionEl) in known)
        {
            ct.ThrowIfCancellationRequested();

            if (!hashes.TryGetValue(hash, out var localPath)) continue;

            var projectId = Str(versionEl, "project_id");
            if (projectId is null) continue;

            var currentVersion = Str(versionEl, "version_number") ?? "";
            var currentDate = Date(versionEl, "date_published");

            try
            {
                var project = new ModSearchResult
                {
                    Provider = ModProvider.Modrinth,
                    ProjectId = projectId,
                    Title = Path.GetFileNameWithoutExtension(localPath)
                };

                var candidates = await GetModrinthFilesAsync(projectId, mcVersion, loader, ct)
                    .ConfigureAwait(false);

                var newest = candidates.FirstOrDefault(f =>
                    f.ReleaseType.Equals("release", StringComparison.OrdinalIgnoreCase))
                    ?? candidates.FirstOrDefault();

                if (newest is null) continue;

                // Обновление есть, если файл другой и он свежее
                var sameFile = string.Equals(newest.FileName, Path.GetFileName(localPath),
                    StringComparison.OrdinalIgnoreCase);
                var newer = currentDate is null || newest.Published > currentDate;

                if (sameFile || !newer) continue;

                // Уточняем название проекта, чтобы в списке было понятно
                var info = await GetProjectAsync(ModProvider.Modrinth, projectId, ct).ConfigureAwait(false);

                updates.Add(new ModUpdate
                {
                    FilePath = localPath,
                    CurrentName = Path.GetFileName(localPath),
                    CurrentVersion = currentVersion,
                    Project = info ?? project,
                    NewFile = newest
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"Обновление для {Path.GetFileName(localPath)}: {ex.Message}");
            }
        }

        return updates;
    }

    /// <summary>Ставит новую версию и удаляет старый файл.</summary>
    public async Task<bool> ApplyUpdateAsync(
        ModUpdate update, string modsDir, string mcVersion, LoaderKind loader,
        CancellationToken ct = default)
    {
        var outcome = await InstallAsync(update.NewFile, modsDir, mcVersion, loader, false, ct)
            .ConfigureAwait(false);

        if (outcome.Installed.Count == 0) return false;

        try
        {
            if (File.Exists(update.FilePath) &&
                !string.Equals(Path.GetFileName(update.FilePath), update.NewFile.FileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(update.FilePath);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось удалить старую версию {update.CurrentName}: {ex.Message}");
        }

        return true;
    }
    // =====================================================================
    //  ЛОКАЛЬНЫЕ МОДЫ
    // =====================================================================

    public sealed class LocalMod
    {
        public required string FilePath { get; init; }
        public required string FileName { get; init; }
        public long Size { get; init; }
        public DateTime Modified { get; init; }
        public bool Enabled { get; init; }

        public string SizeDisplay
        {
            get
            {
                double v = Size;
                string[] u = { "Б", "КБ", "МБ" };
                var i = 0;
                while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
                return $"{v:0.#} {u[i]}";
            }
        }

        public string DisplayName => Enabled
            ? Path.GetFileNameWithoutExtension(FileName)
            : Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(FileName)) + "  (выключен)";
    }

    public static List<LocalMod> GetLocalMods(string modsDir)
    {
        if (!Directory.Exists(modsDir)) return new List<LocalMod>();

        try
        {
            return new DirectoryInfo(modsDir)
                .GetFiles()
                .Where(f => f.Extension.Equals(".jar", StringComparison.OrdinalIgnoreCase) ||
                            f.Name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                .Select(f => new LocalMod
                {
                    FilePath = f.FullName,
                    FileName = f.Name,
                    Size = f.Length,
                    Modified = f.LastWriteTime,
                    Enabled = !f.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                })
                .OrderBy(m => m.FileName)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось прочитать список модов: " + ex.Message);
            return new List<LocalMod>();
        }
    }

    /// <summary>Включает/выключает мод переименованием (.jar &lt;-&gt; .jar.disabled).</summary>
    public static void ToggleMod(LocalMod mod)
    {
        var target = mod.Enabled
            ? mod.FilePath + ".disabled"
            : mod.FilePath[..^".disabled".Length];

        if (File.Exists(target)) File.Delete(target);
        File.Move(mod.FilePath, target);
    }

    public static void DeleteMod(LocalMod mod)
    {
        if (File.Exists(mod.FilePath)) File.Delete(mod.FilePath);
    }

    // =====================================================================
    //  ХЕЛПЕРЫ
    // =====================================================================

    private static string ModrinthProjectType(ModContentType t) => t switch
    {
        ModContentType.ResourcePack => "resourcepack",
        ModContentType.ShaderPack => "shader",
        ModContentType.ModPack => "modpack",
        _ => "mod"
    };

    private static string ModrinthLoader(LoaderKind l) => l switch
    {
        LoaderKind.Fabric => "fabric",
        LoaderKind.Forge => "forge",
        LoaderKind.NeoForge => "neoforge",
        _ => "fabric"
    };

    private static int CurseClassId(ModContentType t) => t switch
    {
        ModContentType.ResourcePack => CurseClassResourcePacks,
        ModContentType.ShaderPack => CurseClassShaders,
        ModContentType.ModPack => CurseClassModpacks,
        _ => CurseClassMods
    };

    /// <summary>modLoaderType: 1=Forge, 4=Fabric, 5=Quilt, 6=NeoForge</summary>
    private static int CurseLoaderType(LoaderKind l) => l switch
    {
        LoaderKind.Forge => 1,
        LoaderKind.Fabric => 4,
        LoaderKind.NeoForge => 6,
        _ => 0
    };

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Num(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0;

    private static DateTimeOffset? Date(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(v.GetString(), out var d) ? d : null;

    private static List<string> StrList(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return new List<string>();

        return v.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();
    }
}
