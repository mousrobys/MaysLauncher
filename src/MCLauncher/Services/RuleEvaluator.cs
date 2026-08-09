using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using MCLauncher.Models;

namespace MCLauncher.Services;

/// <summary>Вычисление правил (rules) из JSON версии для текущей ОС.</summary>
public static class RuleEvaluator
{
    public static string OsName { get; } =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "linux";

    public static string OsArch { get; } = RuntimeInformation.OSArchitecture switch
    {
        Architecture.X86 => "x86",
        Architecture.X64 => "x86_64",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm32",
        _ => "x86_64"
    };

    /// <summary>Ключ для natives: windows -> natives-windows (${arch} = 64/32).</summary>
    public static string NativeArchToken { get; } = Environment.Is64BitOperatingSystem ? "64" : "32";

    private static string OsVersion { get; } = Environment.OSVersion.Version.ToString();

    /// <summary>Разрешена ли сущность (библиотека / аргумент) текущими правилами.</summary>
    public static bool Allows(List<Rule>? rules, IReadOnlyDictionary<string, bool>? features = null)
    {
        if (rules is null || rules.Count == 0) return true;

        // По спецификации Mojang: по умолчанию запрещено, если есть хоть одно правило.
        var allowed = false;

        foreach (var rule in rules)
        {
            if (!RuleMatches(rule, features)) continue;
            allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    private static bool RuleMatches(Rule rule, IReadOnlyDictionary<string, bool>? features)
    {
        if (rule.Os is not null)
        {
            if (!string.IsNullOrEmpty(rule.Os.Name) &&
                !string.Equals(rule.Os.Name, OsName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(rule.Os.Arch) &&
                !string.Equals(rule.Os.Arch, OsArch, StringComparison.OrdinalIgnoreCase) &&
                !(rule.Os.Arch == "x86" && OsArch == "x86_64"))
                return false;

            if (!string.IsNullOrEmpty(rule.Os.Version))
            {
                try
                {
                    if (!Regex.IsMatch(OsVersion, rule.Os.Version!)) return false;
                }
                catch (ArgumentException) { /* некорректный regex — игнорируем условие */ }
            }
        }

        if (rule.Features is not null && rule.Features.Count > 0)
        {
            foreach (var (key, required) in rule.Features)
            {
                var actual = features is not null && features.TryGetValue(key, out var v) && v;
                if (actual != required) return false;
            }
        }

        return true;
    }

    /// <summary>Возвращает classifier для natives текущей ОС, либо null.</summary>
    public static string? GetNativeClassifier(Library lib)
    {
        if (lib.Natives is null) return null;
        if (!lib.Natives.TryGetValue(OsName, out var classifier) || classifier is null) return null;
        return classifier.Replace("${arch}", NativeArchToken);
    }

    /// <summary>
    /// Новый стиль (1.19+): natives приходят как отдельные библиотеки с именем
    /// вида org.lwjgl:lwjgl:3.3.1:natives-windows.
    /// </summary>
    public static bool IsNativeArtifactName(string name)
    {
        var parts = name.Split(':');
        return parts.Length >= 4 && parts[3].StartsWith("natives-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Правила Mojang для natives-windows / natives-windows-x86 / natives-windows-arm64
    /// одинаковы (os=windows), различает их только суффикс архитектуры в classifier.
    /// Без этой проверки 32-битная lwjgl.dll затирает 64-битную и игра падает
    /// с UnsatisfiedLinkError: Failed to locate library: lwjgl.dll
    /// </summary>
    public static bool NativeMatchesCurrentArch(string name)
    {
        var parts = name.Split(':');
        if (parts.Length < 4) return true;

        var classifier = parts[3].ToLowerInvariant();
        if (!classifier.StartsWith("natives-", StringComparison.Ordinal)) return true;

        var isX86 = classifier.EndsWith("-x86", StringComparison.Ordinal);
        var isArm64 = classifier.EndsWith("-arm64", StringComparison.Ordinal);
        var isArm32 = classifier.EndsWith("-arm32", StringComparison.Ordinal);

        return OsArch switch
        {
            "x86_64" => !isX86 && !isArm64 && !isArm32,
            "x86" => isX86,
            "arm64" => isArm64,
            "arm32" => isArm32,
            _ => !isX86 && !isArm64 && !isArm32
        };
    }

    /// <summary>Преобразует maven-имя group:artifact:version[:classifier] в относительный путь.</summary>
    public static string MavenNameToPath(string name)
    {
        var atIdx = name.IndexOf('@');
        var ext = "jar";
        if (atIdx >= 0)
        {
            ext = name[(atIdx + 1)..];
            name = name[..atIdx];
        }

        var parts = name.Split(':');
        if (parts.Length < 3)
            throw new FormatException($"Некорректное maven-имя библиотеки: {name}");

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length >= 4 ? "-" + parts[3] : "";

        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.{ext}";
    }
}
