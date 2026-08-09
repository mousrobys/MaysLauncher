using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MCLauncher.Services;

/// <summary>Готовый пресет цветовой схемы.</summary>
public sealed class ThemePreset
{
    public required string Name { get; init; }
    public required string BgDeep { get; init; }
    public required string Bg { get; init; }
    public required string Panel { get; init; }
    public required string PanelHover { get; init; }
    public required string Border { get; init; }
    public required string Text { get; init; }
    public required string TextMuted { get; init; }
    public bool IsLight { get; init; }
}

/// <summary>
/// Управление внешним видом: цветовая схема, акцент и фоновое изображение.
/// Все ресурсы меняются через DynamicResource, поэтому применяются мгновенно
/// без перезапуска лаунчера.
/// </summary>
public static class ThemeService
{
    public static Color CurrentAccent { get; private set; } =
        (Color)ColorConverter.ConvertFromString("#4ADE80");

    private static ThemePreset? _current;
    public static ThemePreset CurrentTheme => _current ??= Presets[0];

    /// <summary>Встроенные схемы.</summary>
    public static readonly ThemePreset[] Presets =
    {
        new()
        {
            Name = "Тёмная", BgDeep = "#0E1013", Bg = "#14171C", Panel = "#1B1F26",
            PanelHover = "#232833", Border = "#2A2F3A", Text = "#E8EBF0", TextMuted = "#8B93A3"
        },
        new()
        {
            Name = "Полночь", BgDeep = "#080A0F", Bg = "#0D1017", Panel = "#141922",
            PanelHover = "#1B2230", Border = "#232C3B", Text = "#E4E9F2", TextMuted = "#7C879B"
        },
        new()
        {
            Name = "Графит", BgDeep = "#151515", Bg = "#1C1C1C", Panel = "#242424",
            PanelHover = "#2E2E2E", Border = "#363636", Text = "#EAEAEA", TextMuted = "#909090"
        },
        new()
        {
            Name = "Океан", BgDeep = "#0A1420", Bg = "#0F1C2B", Panel = "#152538",
            PanelHover = "#1D3148", Border = "#243D57", Text = "#E3EDF7", TextMuted = "#7F94AB"
        },
        new()
        {
            Name = "Вино", BgDeep = "#140C10", Bg = "#1B1116", Panel = "#24171E",
            PanelHover = "#2F1E27", Border = "#3A2530", Text = "#F0E6EA", TextMuted = "#A18B95"
        },
        new()
        {
            Name = "Лес", BgDeep = "#0B1410", Bg = "#101B15", Panel = "#16241C",
            PanelHover = "#1E3126", Border = "#264031", Text = "#E4F0E8", TextMuted = "#7E9A8A"
        },
        new()
        {
            Name = "Кофе", BgDeep = "#14100C", Bg = "#1C1711", Panel = "#251E17",
            PanelHover = "#31281F", Border = "#3D3126", Text = "#F0E9E0", TextMuted = "#A0917F"
        },
        new()
        {
            Name = "Неон", BgDeep = "#0A0A12", Bg = "#0F0F1C", Panel = "#161628",
            PanelHover = "#1F1F38", Border = "#2A2A4A", Text = "#E8E8FF", TextMuted = "#8888B0"
        },
        new()
        {
            Name = "Сталь", BgDeep = "#111417", Bg = "#171B20", Panel = "#1F252B",
            PanelHover = "#283038", Border = "#333C46", Text = "#E6EAEF", TextMuted = "#8794A1"
        },
        new()
        {
            Name = "Сакура", BgDeep = "#160F14", Bg = "#1E151B", Panel = "#281C24",
            PanelHover = "#35262F", Border = "#42303B", Text = "#F5E8EF", TextMuted = "#AC8D9E"
        },
        new()
        {
            Name = "Светлая", BgDeep = "#EEF1F5", Bg = "#F6F8FA", Panel = "#FFFFFF",
            PanelHover = "#EDF1F6", Border = "#D8DEE6", Text = "#1B2028", TextMuted = "#5D6875",
            IsLight = true
        },
        new()
        {
            Name = "Бумага", BgDeep = "#EDE8DE", Bg = "#F5F1E8", Panel = "#FFFCF5",
            PanelHover = "#F0EADD", Border = "#DDD5C6", Text = "#2B2620", TextMuted = "#6B6255",
            IsLight = true
        }
    };

    /// <summary>Пользовательская схема, если выбрана «Своя».</summary>
    public static ThemePreset? CustomPreset { get; set; }

    public const string CustomThemeName = "Своя";

    /// <summary>Все схемы, включая пользовательскую.</summary>
    public static IEnumerable<ThemePreset> AllPresets()
    {
        foreach (var p in Presets) yield return p;
        if (CustomPreset is not null) yield return CustomPreset;
    }

    // =====================================================================
    //  ПРИМЕНЕНИЕ
    // =====================================================================

    public static void ApplyTheme(string themeName)
    {
        var preset = AllPresets().FirstOrDefault(p =>
                         string.Equals(p.Name, themeName, StringComparison.OrdinalIgnoreCase))
                     ?? Presets[0];

        _current = preset;

        var res = Application.Current?.Resources;
        if (res is null) return;

        SetBrush(res, "BgDeep", preset.BgDeep);
        SetBrush(res, "Bg", preset.Bg);
        SetBrush(res, "Panel", preset.Panel);
        SetBrush(res, "PanelHover", preset.PanelHover);
        SetBrush(res, "BorderBrushDark", preset.Border);
        SetBrush(res, "Fg", preset.Text);
        SetBrush(res, "FgMuted", preset.TextMuted);

        res["BgDeepColor"] = ToColor(preset.BgDeep);
        res["BgColor"] = ToColor(preset.Bg);
        res["PanelColor"] = ToColor(preset.Panel);
        res["TextColor"] = ToColor(preset.Text);

        // Цвет текста на акцентной кнопке зависит от светлоты темы
        res["OnAccent"] = Freeze(new SolidColorBrush(
            preset.IsLight ? Colors.White : (Color)ColorConverter.ConvertFromString("#08130C")));

        // Поле ввода и лог на светлой теме нужно перекрасить отдельно
        res["ConsoleBg"] = Freeze(new SolidColorBrush(ToColor(preset.IsLight ? "#FFFFFF" : "#0B0D10")));
        res["ConsoleFg"] = Freeze(new SolidColorBrush(ToColor(preset.IsLight ? "#39424F" : "#A8B4C4")));
    }

    public static void ApplyAccent(string hex)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hex)) return;
            if (!hex.StartsWith('#')) hex = "#" + hex;

            var color = (Color)ColorConverter.ConvertFromString(hex);
            CurrentAccent = color;

            var res = Application.Current?.Resources;
            if (res is null) return;

            res["AccentColor"] = color;
            res["Accent"] = Freeze(new SolidColorBrush(color));
            res["AccentDark"] = Freeze(new SolidColorBrush(Darken(color, 0.82)));
            res["AccentLight"] = Freeze(new SolidColorBrush(Lighten(color, 0.22)));
            res["AccentGlow"] = Freeze(new SolidColorBrush(Color.FromArgb(38, color.R, color.G, color.B)));
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось применить акцентный цвет: " + ex.Message);
        }
    }

    /// <summary>
    /// Фон всего окна: своя картинка поверх базового цвета.
    /// Прозрачность регулируется, чтобы текст оставался читаемым.
    /// </summary>
    public static ImageBrush? BuildWindowBackground(string imagePath, double opacity)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) return null;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(imagePath);
            bmp.DecodePixelWidth = 1920;
            bmp.EndInit();
            bmp.Freeze();

            var brush = new ImageBrush(bmp)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center,
                Opacity = Math.Clamp(opacity, 0.05, 1.0)
            };
            brush.Freeze();
            return brush;
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось загрузить фон окна: " + ex.Message);
            return null;
        }
    }

    /// <summary>Градиент для баннера главного экрана.</summary>
    public static LinearGradientBrush BuildBanner(string style, Color accent)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        var t = CurrentTheme;

        switch (style)
        {
            case "Ночь":
                brush.GradientStops.Add(new GradientStop(ToColor(t.Bg), 0));
                brush.GradientStops.Add(new GradientStop(Lighten(ToColor(t.Panel), 0.05), 1));
                break;

            case "Космос":
                brush.GradientStops.Add(new GradientStop(ToColor("#1B1436"), 0));
                brush.GradientStops.Add(new GradientStop(ToColor("#0E1230"), 0.6));
                brush.GradientStops.Add(new GradientStop(ToColor("#231A3D"), 1));
                break;

            case "Закат":
                brush.GradientStops.Add(new GradientStop(ToColor("#3A1F1A"), 0));
                brush.GradientStops.Add(new GradientStop(ToColor("#1A1620"), 0.6));
                brush.GradientStops.Add(new GradientStop(ToColor("#2A1B2E"), 1));
                break;

            case "Графит":
                brush.GradientStops.Add(new GradientStop(ToColor(t.Panel), 0));
                brush.GradientStops.Add(new GradientStop(ToColor(t.BgDeep), 1));
                break;

            default: // «Изумруд» — окрашивается текущим акцентом
                brush.GradientStops.Add(new GradientStop(
                    t.IsLight ? Lighten(accent, 0.55) : Darken(accent, 0.28), 0));
                brush.GradientStops.Add(new GradientStop(ToColor(t.Bg), 0.55));
                brush.GradientStops.Add(new GradientStop(ToColor(t.Panel), 1));
                break;
        }

        brush.Freeze();
        return brush;
    }

    public static readonly string[] BackgroundStyles = { "Изумруд", "Ночь", "Космос", "Закат", "Графит" };

    // =====================================================================
    //  ХЕЛПЕРЫ
    // =====================================================================

    private static void SetBrush(ResourceDictionary res, string key, string hex) =>
        res[key] = Freeze(new SolidColorBrush(ToColor(hex)));

    private static Color ToColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    public static Color Darken(Color c, double factor) => Color.FromRgb(
        (byte)Math.Clamp(c.R * factor, 0, 255),
        (byte)Math.Clamp(c.G * factor, 0, 255),
        (byte)Math.Clamp(c.B * factor, 0, 255));

    public static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)Math.Clamp(c.R + (255 - c.R) * amount, 0, 255),
        (byte)Math.Clamp(c.G + (255 - c.G) * amount, 0, 255),
        (byte)Math.Clamp(c.B + (255 - c.B) * amount, 0, 255));
}
