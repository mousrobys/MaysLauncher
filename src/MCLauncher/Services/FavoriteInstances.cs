using System.IO;
using System.Text.Json;

namespace MCLauncher.Services;


public class FavoriteInstances
{
    private static string DataFile => Path.Combine(LauncherPaths.Root, "favorites.json");
    private HashSet<string> _ids = new();

    public bool IsFavorite(string instanceId) => _ids.Contains(instanceId);

    public void Toggle(string instanceId)
    {
        if (_ids.Contains(instanceId)) _ids.Remove(instanceId);
        else _ids.Add(instanceId);
        Save();
    }

    public List<string> GetAll() => _ids.ToList();

    public void Load()
    {
        try
        {
            if (System.IO.File.Exists(DataFile))
                _ids = JsonSerializer.Deserialize<HashSet<string>>(System.IO.File.ReadAllText(DataFile)) ?? new();
        }
        catch { _ids = new(); }
    }

    private void Save()
    {
        try { System.IO.File.WriteAllText(DataFile, JsonSerializer.Serialize(_ids)); }
        catch { }
    }
}
