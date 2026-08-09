using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCLauncher.Models;

namespace MCLauncher.Services;

/// <summary>Сохранение сессии на диск с шифрованием DPAPI (привязка к пользователю Windows).</summary>
public static class AccountStorage
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Mays.MCLauncher.v1");

    public static void Save(MinecraftAccount account)
    {
        try
        {
            LauncherPaths.EnsureAll();
            var json = JsonSerializer.SerializeToUtf8Bytes(account);
            var protectedBytes = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);

            // Атомарная запись: обрыв на середине не должен обнулить сессию
            var tmp = LauncherPaths.AccountFile + ".tmp";
            File.WriteAllBytes(tmp, protectedBytes);

            if (File.Exists(LauncherPaths.AccountFile))
            {
                try { File.Copy(LauncherPaths.AccountFile, LauncherPaths.AccountFile + ".bak", true); } catch { }
                File.Replace(tmp, LauncherPaths.AccountFile, null);
            }
            else File.Move(tmp, LauncherPaths.AccountFile);
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось сохранить аккаунт: " + ex.Message);
        }
    }

    public static MinecraftAccount? Load()
    {
        try
        {
            foreach (var file in new[] { LauncherPaths.AccountFile, LauncherPaths.AccountFile + ".bak" })
            {
                if (!File.Exists(file)) continue;

                try
                {
                    var protectedBytes = File.ReadAllBytes(file);
                    if (protectedBytes.Length == 0) continue;

                    var json = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                    var acc = JsonSerializer.Deserialize<MinecraftAccount>(json);
                    if (acc is null || string.IsNullOrEmpty(acc.Username)) continue;

                    if (file.EndsWith(".bak", StringComparison.Ordinal))
                        Log.Warn("Основной файл аккаунта повреждён — восстановлен из копии.");

                    return acc;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Не удалось прочитать {Path.GetFileName(file)}: {ex.Message}");
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось прочитать сохранённый аккаунт: " + ex.Message);
            return null;
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(LauncherPaths.AccountFile))
                File.Delete(LauncherPaths.AccountFile);
        }
        catch { /* ignore */ }
    }
}
