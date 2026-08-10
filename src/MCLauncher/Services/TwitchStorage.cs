using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace MCLauncher.Services;

public static class TwitchStorage
{
    private static string DataFile => Path.Combine(LauncherPaths.Root, "twitch_session.json");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Mays.Twitch.v1");

    public static TwitchAccount? Load()
    {
        try
        {
            if (!System.IO.File.Exists(DataFile)) return null;
            var encrypted = System.IO.File.ReadAllBytes(DataFile);
            var decrypted = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<TwitchAccount>(decrypted);
        }
        catch { return null; }
    }

    public static void Save(TwitchAccount? account)
    {
        try
        {
            if (account == null) { Clear(); return; }
            var json = JsonSerializer.SerializeToUtf8Bytes(account);
            var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(LauncherPaths.Root);
            System.IO.File.WriteAllBytes(DataFile, encrypted);
        }
        catch { }
    }

    public static void Clear()
    {
        try { if (System.IO.File.Exists(DataFile)) System.IO.File.Delete(DataFile); }
        catch { }
    }
}
