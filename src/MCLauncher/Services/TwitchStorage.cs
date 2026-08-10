using System.IO;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace MCLauncher.Services;

public static class TwitchStorage
{
    private static string FilePath => Path.Combine(LauncherPaths.Root, "twitch_session.json");
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("Mays.Twitch.v1");

    public static TwitchAccount? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var encrypted = File.ReadAllBytes(FilePath);
            var decrypted = ProtectedData.Unprotect(encrypted, Salt, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<TwitchAccount>(decrypted);
        }
        catch { return null; }
    }

    public static void Save(TwitchAccount? account)
    {
        try
        {
            if (account == null) { Clear(); return; }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(account);
            var encrypted = ProtectedData.Protect(bytes, Salt, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(LauncherPaths.Root);
            File.WriteAllBytes(FilePath, encrypted);
        }
        catch { }
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch { }
    }
}
