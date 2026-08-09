using System.Text.Json;
using MCLauncher.Models;

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
}
