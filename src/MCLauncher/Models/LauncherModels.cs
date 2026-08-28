using System.Text.Json.Serialization;

namespace MCLauncher.Models;

// ======================= МОДЛОАДЕРЫ =======================

public enum LoaderKind
{
    Vanilla = 0,
    Fabric = 1,
    Forge = 2,
    NeoForge = 3
}

public static class LoaderKindExtensions
{
    public static string Display(this LoaderKind k) => k switch
    {
        LoaderKind.Vanilla => "Vanilla",
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        _ => k.ToString()
    };
}

/// <summary>Доступная версия модлоадера для конкретной версии игры.</summary>
public sealed class LoaderVersion
{
    public required LoaderKind Kind { get; init; }
    public required string Version { get; init; }
    public string? McVersion { get; init; }
    public bool IsStable { get; init; } = true;
    public bool IsRecommended { get; init; }

    public override string ToString() =>
        Version + (IsRecommended ? "  (рекомендуется)" : IsStable ? "" : "  (beta)");
}

// ======================= ИНСТАНС (СБОРКА) =======================

/// <summary>
/// Сборка — изолированный набор: версия игры + модлоадер + своя папка
/// с модами, ресурспаками, шейдерами, скриншотами и сохранениями.
/// </summary>
public sealed class GameInstance
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    [JsonPropertyName("name")] public string Name { get; set; } = "Новая сборка";
    [JsonPropertyName("mcVersion")] public string McVersion { get; set; } = "";
    [JsonPropertyName("loader")] public LoaderKind Loader { get; set; } = LoaderKind.Vanilla;
    [JsonPropertyName("loaderVersion")] public string? LoaderVersion { get; set; }

    /// <summary>Идентификатор версии для запуска (у модлоадеров отличается от McVersion).</summary>
    [JsonPropertyName("launchVersionId")] public string? LaunchVersionId { get; set; }

    [JsonPropertyName("created")] public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    [JsonPropertyName("lastPlayed")] public DateTimeOffset? LastPlayed { get; set; }
    [JsonPropertyName("totalPlaySeconds")] public long TotalPlaySeconds { get; set; }

    /// <summary>Индивидуальная память; 0 = использовать глобальную настройку.</summary>
    [JsonPropertyName("maxMemoryMb")] public int MaxMemoryMb { get; set; }

    [JsonPropertyName("extraJvmArgs")] public string ExtraJvmArgs { get; set; } = "";
    [JsonPropertyName("iconColor")] public string IconColor { get; set; } = "#A855F7";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";

    /// <summary>
    /// Полная изоляция: сборка держит собственные libraries, assets, natives и versions.
    /// Занимает больше места, зато обновление или поломка одной сборки не заденет остальные.
    /// </summary>
    [JsonPropertyName("isolated")] public bool Isolated { get; set; }

    /// <summary>Индивидуальный путь к java.exe; пусто = глобальная настройка.</summary>
    [JsonPropertyName("javaPath")] public string JavaPath { get; set; } = "";

    /// <summary>Индивидуальное разрешение окна; 0 = глобальное.</summary>
    [JsonPropertyName("windowWidth")] public int WindowWidth { get; set; }
    [JsonPropertyName("windowHeight")] public int WindowHeight { get; set; }

    /// <summary>Автоподключение к серверу при запуске этой сборки.</summary>
    [JsonPropertyName("serverAddress")] public string ServerAddress { get; set; } = "";

    /// <summary>Путь к своей иконке сборки (png/jpg). Пусто — цветная точка.</summary>
    [JsonPropertyName("iconPath")] public string IconPath { get; set; } = "";

    /// <summary>Активный профиль модов.</summary>
    [JsonPropertyName("activeModProfile")] public string ActiveModProfile { get; set; } = "По умолчанию";

    /// <summary>Пресет аргументов JVM.</summary>
    [JsonPropertyName("jvmPreset")] public string JvmPreset { get; set; } = "Стандарт";

    /// <summary>История игровых сессий для статистики.</summary>
    [JsonPropertyName("sessions")] public List<PlaySession> Sessions { get; set; } = new();

    /// <summary>Записывает завершённую сессию и обновляет счётчики.</summary>
    public void AddSession(long seconds)
    {
        if (seconds < 5) return;   // случайные запуски не считаем

        TotalPlaySeconds += seconds;
        LastPlayed = DateTimeOffset.Now;

        Sessions.Add(new PlaySession
        {
            Date = DateTimeOffset.Now,
            Seconds = seconds
        });

        // Храним полгода, иначе файл разрастётся
        var limit = DateTimeOffset.Now.AddDays(-180);
        Sessions.RemoveAll(s => s.Date < limit);
    }

    [JsonIgnore] public string EffectiveVersionId => LaunchVersionId ?? McVersion;

    /// <summary>Кисть цветной метки — готовая, чтобы не биндиться к Color внутри Brush.</summary>
    [JsonIgnore]
    public object IconBrush
    {
        get
        {
            try
            {
                var color = (System.Windows.Media.Color)
                    System.Windows.Media.ColorConverter.ConvertFromString(IconColor);

                var brush = new System.Windows.Media.SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
            catch
            {
                return System.Windows.Media.Brushes.Gray;
            }
        }
    }

    /// <summary>Картинка для списка. Кэшируется, чтобы не читать файл на каждую перерисовку.</summary>
    [JsonIgnore]
    public object? IconImage
    {
        get
        {
            if (string.IsNullOrWhiteSpace(IconPath) || !File.Exists(IconPath)) return null;

            var stamp = File.GetLastWriteTimeUtc(IconPath);
            if (_iconCache is not null && _iconStamp == stamp) return _iconCache;

            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 48;
                bmp.UriSource = new Uri(IconPath);
                bmp.EndInit();
                bmp.Freeze();

                _iconCache = bmp;
                _iconStamp = stamp;
                return bmp;
            }
            catch { return null; }
        }
    }

    private object? _iconCache;
    private DateTime _iconStamp;

    [JsonIgnore]
    public string LoaderDisplay => Loader == LoaderKind.Vanilla
        ? "Vanilla"
        : $"{Loader.Display()} {LoaderVersion}";

    [JsonIgnore]
    public string PlayTimeDisplay
    {
        get
        {
            if (TotalPlaySeconds < 60) return "менее минуты";
            var ts = TimeSpan.FromSeconds(TotalPlaySeconds);
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours} ч {ts.Minutes} мин";
            return $"{ts.Minutes} мин";
        }
    }

    public override string ToString() => Name;
}

/// <summary>Одна игровая сессия — для графика по дням.</summary>
public sealed class PlaySession
{
    [JsonPropertyName("date")] public DateTimeOffset Date { get; set; }
    [JsonPropertyName("seconds")] public long Seconds { get; set; }
}

// ======================= СЕРВЕРЫ =======================

public sealed class ServerEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("version")] public string RequiredVersion { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("site")] public string? Site { get; set; }
    [JsonPropertyName("featured")] public bool Featured { get; set; }
    [JsonPropertyName("loader")] public LoaderKind Loader { get; set; } = LoaderKind.Vanilla;
}

/// <summary>Результат пинга сервера по Server List Ping.</summary>
public sealed class ServerStatus
{
    public bool Online { get; init; }
    public int OnlinePlayers { get; init; }
    public int MaxPlayers { get; init; }
    public string VersionName { get; init; } = "";
    public int ProtocolVersion { get; init; }
    public string Motd { get; init; } = "";
    public long PingMs { get; init; }
    public byte[]? FaviconPng { get; init; }
    public string? Error { get; init; }

    public static ServerStatus Offline(string error) => new() { Online = false, Error = error };

    public string PlayersDisplay => Online ? $"{OnlinePlayers} / {MaxPlayers}" : "—";
}
