using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MCLauncher.Models;

namespace MCLauncher.Services;

/// <summary>
/// Скины для оффлайн-аккаунтов через мод CustomSkinLoader (Modrinth: idMHQ4n2).
/// Мод умеет грузить локальные скины из папки <GameDir>/CustomSkinLoader/LocalSkin/skins/&lt;ник&gt;.png
/// без обращения к Mojang. Работает только в сборках с модлоадером (Fabric/Forge/NeoForge).
/// </summary>
public static class OfflineSkinService
{
    private const string CslProject = "customskinloader";
    private const string CslLocalRelative = "CustomSkinLoader/LocalSkin/skins";

    /// <summary>Слот скина оффлайн-аккаунта: %APPDATA%\.mayslauncher\skins\&lt;ник&gt;.png</summary>
    public static string AccountSkinPath(string username) =>
        Path.Combine(LauncherPaths.Root, "skins", username + ".png");

    /// <summary>Находит файл скина слота, без учёта регистра ника.</summary>
    public static string? FindAccountSkin(string username)
    {
        var exact = AccountSkinPath(username);
        if (File.Exists(exact)) return exact;

        var dir = Path.Combine(LauncherPaths.Root, "skins");
        if (!Directory.Exists(dir)) return null;

        return Directory.GetFiles(dir, "*.png")
            .FirstOrDefault(f => string.Equals(
                Path.GetFileNameWithoutExtension(f), username, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Поддерживает ли сборка мод CustomSkinLoader (нужен модлоадер).</summary>
    public static bool IsCslSupported(GameInstance inst) => inst.Loader != LoaderKind.Vanilla;

    /// <summary>Есть ли уже CustomSkinLoader в папке mods сборки.</summary>
    public static bool IsCslInstalled(GameInstance inst)
    {
        var modsDir = InstanceService.ModsDir(inst);
        if (!Directory.Exists(modsDir)) return false;
        return Directory.GetFiles(modsDir, "*.jar")
            .Any(f => Path.GetFileName(f).Contains("customskinloader", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Путь локального скина в папке CustomSkinLoader сборки.</summary>
    public static string InstanceSkinPath(GameInstance inst, string username) =>
        Path.Combine(InstanceService.InstanceDir(inst), CslLocalRelative, username + ".png");

    /// <summary>Копирует скин в папку CustomSkinLoader сборки (основной + вариант в нижнем регистре).</summary>
    public static void SyncToInstance(GameInstance inst, string username, string skinFile)
    {
        var targetDir = Path.Combine(InstanceService.InstanceDir(inst), CslLocalRelative);
        Directory.CreateDirectory(targetDir);
        File.Copy(skinFile, Path.Combine(targetDir, username + ".png"), overwrite: true);
        File.Copy(skinFile, Path.Combine(targetDir, username.ToLowerInvariant() + ".png"), overwrite: true);
    }

    /// <summary>Удаляет локальный скин CustomSkinLoader из сборки (оба варианта регистра ника).</summary>
    public static void RemoveFromInstance(GameInstance inst, string username)
    {
        var targetDir = Path.Combine(InstanceService.InstanceDir(inst), CslLocalRelative);
        if (!Directory.Exists(targetDir)) return;

        foreach (var name in new[] { username, username.ToLowerInvariant() })
        {
            var file = Path.Combine(targetDir, name + ".png");
            if (File.Exists(file)) File.Delete(file);
        }
    }

    /// <summary>
    /// Ставит CustomSkinLoader в сборку, если его там нет. Возвращает true, если мод готов к использованию.
    /// </summary>
    public static async Task<bool> EnsureCslModAsync(GameInstance inst, CancellationToken ct = default)
    {
        if (!IsCslSupported(inst)) return false;
        if (IsCslInstalled(inst)) return true;
        if (string.IsNullOrEmpty(inst.McVersion)) return false;

        var loader = inst.Loader switch
        {
            LoaderKind.Fabric => "fabric",
            LoaderKind.Forge => "forge",
            LoaderKind.NeoForge => "neoforge",
            _ => null
        };
        if (loader == null) return false;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Add("User-Agent", "MaysLauncher/1.0");

            var url = $"https://api.modrinth.com/v2/project/{CslProject}/version" +
                      $"?game_versions={Uri.EscapeDataString("[\"" + inst.McVersion + "\"]")}" +
                      $"&loaders={Uri.EscapeDataString("[\"" + loader + "\"]")}";

            var json = await http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;

            string? fileUrl = null;
            foreach (var v in doc.RootElement.EnumerateArray())
            {
                if (!v.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) continue;
                foreach (var f in files.EnumerateArray())
                {
                    if (f.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                    {
                        fileUrl = u.GetString();
                        break;
                    }
                }
                if (fileUrl != null) break;
            }

            if (string.IsNullOrEmpty(fileUrl)) return false;

            Directory.CreateDirectory(InstanceService.ModsDir(inst));
            var target = Path.Combine(InstanceService.ModsDir(inst), "CustomSkinLoader.jar");
            var data = await http.GetByteArrayAsync(fileUrl, ct);
            await File.WriteAllBytesAsync(target, data, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
