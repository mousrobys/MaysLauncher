using System.Text.Json;
using MCLauncher.Models;
using System.Net.Http;

namespace MCLauncher.Services;

/// <summary>Список серверов лаунчера: встроенные (рекламные) + добавленные пользователем.</summary>
public static class ServerCatalog
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string UserServersFile => Path.Combine(LauncherPaths.Root, "servers.json");

    /// <summary>Партнёрские серверы, встроенные в лаунчер.</summary>
    public static readonly ServerEntry[] Featured =
    {
        new()
        {
            Name = "Akimine",
            Address = "mc.akimine.ru",
            RequiredVersion = "26.2",
            Description = "Выживание · анархия · без привилегий за донат. Основной сервер лаунчера.",
            Site = "https://akimine.ru",
            Featured = true,
            Loader = LoaderKind.Vanilla
        }
    };

    public static List<ServerEntry> LoadUserServers()
    {
        try
        {
            if (!File.Exists(UserServersFile)) return new List<ServerEntry>();
            return JsonSerializer.Deserialize<List<ServerEntry>>(File.ReadAllText(UserServersFile))
                   ?? new List<ServerEntry>();
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось прочитать список серверов: " + ex.Message);
            return new List<ServerEntry>();
        }
    }

    public static void SaveUserServers(IEnumerable<ServerEntry> servers)
    {
        try
        {
            LauncherPaths.EnsureAll();
            File.WriteAllText(UserServersFile, JsonSerializer.Serialize(servers.ToList(), Opts));
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось сохранить список серверов: " + ex.Message);
        }
    }

    /// <summary>Все серверы: сначала партнёрские, затем пользовательские.</summary>
    public static List<ServerEntry> LoadAll()
    {
        var list = new List<ServerEntry>(Featured);
        list.AddRange(LoadUserServers());
        return list;
    }

    /// <summary>Загрузить спонсорские серверы из удалённого конфига.</summary>
    public static async Task<List<ServerEntry>> LoadSponsorServersAsync(HttpClient http)
    {
        try
        {
            const string url = "https://raw.githubusercontent.com/mousrobys/MaysLauncher/master/launcher-config.json";
            var response = await http.GetStringAsync(url);
            var config = System.Text.Json.JsonSerializer.Deserialize<LauncherConfig>(response, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (config?.SponsorServers == null) return new List<ServerEntry>();

            return config.SponsorServers.Select(s => new ServerEntry
            {
                Name = s.Name,
                Address = s.Address,
                Description = s.Description,
                Site = s.Site,
                RequiredVersion = s.RequiredVersion,
                Featured = s.Featured,
                Loader = LoaderKind.Vanilla
            }).ToList();
        }
        catch
        {
            return new List<ServerEntry>();
        }
    }
}

public class LauncherConfig
{
    public List<SponsorServerEntry> SponsorServers { get; set; } = new();
}

public class SponsorServerEntry
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string Description { get; set; } = "";
    public string Site { get; set; } = "";
    public string RequiredVersion { get; set; } = "";
    public bool Featured { get; set; } = true;
}


