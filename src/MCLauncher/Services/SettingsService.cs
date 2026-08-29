using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCLauncher.Services;

public sealed class LauncherSettings
{
    [JsonPropertyName("lastVersion")] public string? LastVersion { get; set; }
    [JsonPropertyName("lastInstanceId")] public string? LastInstanceId { get; set; }
    [JsonPropertyName("minMemoryMb")] public int MinMemoryMb { get; set; } = 1024;
    [JsonPropertyName("maxMemoryMb")] public int MaxMemoryMb { get; set; } = 4096;
    [JsonPropertyName("windowWidth")] public int WindowWidth { get; set; } = 1280;
    [JsonPropertyName("windowHeight")] public int WindowHeight { get; set; } = 720;
    [JsonPropertyName("fullscreen")] public bool Fullscreen { get; set; }
    [JsonPropertyName("showSnapshots")] public bool ShowSnapshots { get; set; }
    [JsonPropertyName("closeOnLaunch")] public bool CloseLauncherOnStart { get; set; }
    [JsonPropertyName("showConsole")] public bool ShowConsole { get; set; }
    [JsonPropertyName("serverAddress")] public string ServerAddress { get; set; } = "";
    [JsonPropertyName("extraJvmArgs")] public string ExtraJvmArgs { get; set; } = "";
    [JsonPropertyName("gameDir")] public string GameDir { get; set; } = "";
    [JsonPropertyName("customJavaPath")] public string CustomJavaPath { get; set; } = "";

    // ---------- Поведение при запуске ----------

    /// <summary>Разрешить запускать несколько копий игры одновременно. По умолчанию выключено.</summary>
    [JsonPropertyName("allowMultipleInstances")] public bool AllowMultipleInstances { get; set; }

    /// <summary>Сворачивать лаунчер при старте игры.</summary>
    [JsonPropertyName("minimizeOnLaunch")] public bool MinimizeOnLaunch { get; set; } = true;

    /// <summary>Спрашивать подтверждение перед закрытием игры.</summary>
    [JsonPropertyName("confirmGameStop")] public bool ConfirmGameStop { get; set; } = true;

    // ---------- Внешний вид ----------

    /// <summary>Цвет акцента в формате #RRGGBB.</summary>
    [JsonPropertyName("accentColor")] public string AccentColor { get; set; } = "#4ADE80";

    /// <summary>Пресет фона главного экрана.</summary>
    [JsonPropertyName("backgroundStyle")] public string BackgroundStyle { get; set; } = "Изумруд";

    /// <summary>Путь к своей картинке-баннеру (необязательно).</summary>
    [JsonPropertyName("customBannerPath")] public string CustomBannerPath { get; set; } = "";

    /// <summary>Скругление углов интерфейса.</summary>
    [JsonPropertyName("cornerRadius")] public int CornerRadius { get; set; } = 12;

    /// <summary>Показывать анимации.</summary>
    [JsonPropertyName("animations")] public bool Animations { get; set; } = true;

    /// <summary>Компактный режим боковой панели.</summary>
    [JsonPropertyName("compactMode")] public bool CompactMode { get; set; }

    /// <summary>Создавать новые сборки изолированными (своё хранилище файлов).</summary>
    [JsonPropertyName("defaultIsolated")] public bool DefaultIsolated { get; set; }

    /// <summary>Название цветовой схемы.</summary>
    [JsonPropertyName("theme")] public string Theme { get; set; } = "Тёмная";

    /// <summary>Своя картинка на фон всего окна.</summary>
    [JsonPropertyName("windowBackground")] public string WindowBackgroundPath { get; set; } = "";

    /// <summary>Насыщенность фоновой картинки (0.05–1.0).</summary>
    [JsonPropertyName("windowBackgroundOpacity")] public double WindowBackgroundOpacity { get; set; } = 0.35;

    /// <summary>Язык игры по умолчанию для новых сборок.</summary>
    [JsonPropertyName("gameLanguage")] public string GameLanguage { get; set; } = "ru";

    /// <summary>Ставить язык автоматически при первом запуске сборки.</summary>
    [JsonPropertyName("autoLanguage")] public bool AutoSetGameLanguage { get; set; } = true;

    /// <summary>Сохранённая пользовательская цветовая схема (JSON).</summary>
    [JsonPropertyName("customTheme")] public string CustomThemeJson { get; set; } = "";

    public static int RecommendedMaxMemory()
    {
        try
        {
            var totalMb = (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));
            if (totalMb <= 0) return 4096;
            var half = (int)(totalMb / 2);
            return Math.Clamp(half - half % 512, 2048, 8192);
        }
        catch { return 4096; }
    }

    /// <summary>Доступные пресеты акцентов: название -> HEX.</summary>
    public static readonly (string Name, string Hex)[] AccentPresets =
    {
        ("Изумруд", "#4ADE80"),
        ("Океан", "#38BDF8"),
        ("Аметист", "#A78BFA"),
        ("Закат", "#FB923C"),
        ("Роза", "#FB7185"),
        ("Золото", "#FACC15"),
        ("Бирюза", "#2DD4BF"),
        ("Индиго", "#818CF8")
    };
}

public static class SettingsService
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static LauncherSettings Load()
    {
        try
        {
            if (File.Exists(LauncherPaths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<LauncherSettings>(
                    File.ReadAllText(LauncherPaths.SettingsFile));
                if (s is not null)
                {
                    if (string.IsNullOrWhiteSpace(s.GameDir)) s.GameDir = LauncherPaths.Root;
                    if (string.IsNullOrWhiteSpace(s.AccentColor)) s.AccentColor = "#4ADE80";
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось прочитать настройки: " + ex.Message);
        }

        return new LauncherSettings
        {
            MaxMemoryMb = LauncherSettings.RecommendedMaxMemory(),
            GameDir = LauncherPaths.Root
        };
    }

    public static void Save(LauncherSettings settings)
    {
        try
        {
            LauncherPaths.EnsureAll();
            File.WriteAllText(LauncherPaths.SettingsFile, JsonSerializer.Serialize(settings, Opts));
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось сохранить настройки: " + ex.Message);
        }
    }
}
