using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MCLauncher.Models;

namespace MCLauncher.Services;

/// <summary>
/// Создание локальных (оффлайн) учётных записей.
/// UUID вычисляется тем же алгоритмом, что использует сам Minecraft-сервер
/// в режиме online-mode=false: UUID версии 3 (MD5) от строки "OfflinePlayer:&lt;ник&gt;".
/// Благодаря этому инвентарь и мировые данные совпадают с ванильным поведением.
/// </summary>
public static class OfflineAccountService
{
    public const int MinNameLength = 3;
    public const int MaxNameLength = 16;

    private static readonly Regex ValidName = new(@"^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);

    /// <summary>Проверяет ник по правилам Mojang. Возвращает false и текст ошибки.</summary>
    public static bool TryValidateName(string? name, out string error)
    {
        error = "";
        var n = (name ?? "").Trim();

        if (n.Length == 0)
        {
            error = "Введите никнейм.";
            return false;
        }

        if (n.Length < MinNameLength)
        {
            error = $"Никнейм слишком короткий (минимум {MinNameLength} символа).";
            return false;
        }

        if (n.Length > MaxNameLength)
        {
            error = $"Никнейм слишком длинный (максимум {MaxNameLength} символов).";
            return false;
        }

        if (!ValidName.IsMatch(n))
        {
            error = "Допустимы только латинские буквы, цифры и подчёркивание.";
            return false;
        }

        return true;
    }

    /// <summary>Создаёт оффлайн-аккаунт с UUID, как у ванильного сервера.</summary>
    public static MinecraftAccount Create(string name)
    {
        if (!TryValidateName(name, out var error))
            throw new ArgumentException(error, nameof(name));

        var trimmed = name.Trim();
        var uuid = GenerateOfflineUuid(trimmed);

        return new MinecraftAccount
        {
            Username = trimmed,
            Uuid = uuid,
            // Ванильный клиент принимает любую непустую строку токена в оффлайне.
            AccessToken = "0",
            ExpiresAt = DateTimeOffset.MaxValue,
            MicrosoftRefreshToken = null,
            Xuid = null,
            Type = AccountType.Offline
        };
    }

    /// <summary>
    /// UUID v3 (MD5) от "OfflinePlayer:&lt;ник&gt;" — точная реализация
    /// java.util.UUID.nameUUIDFromBytes из Minecraft.
    /// </summary>
    public static string GenerateOfflineUuid(string name)
    {
        var data = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));

        // Выставляем версию 3 и вариант IETF, как это делает Java.
        data[6] = (byte)((data[6] & 0x0F) | 0x30);
        data[8] = (byte)((data[8] & 0x3F) | 0x80);

        return Convert.ToHexString(data).ToLowerInvariant();
    }
}
