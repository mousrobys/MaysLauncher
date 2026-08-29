using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MCLauncher.Services;

namespace MCLauncher.Views;

/// <summary>Ручная настройка цветовой схемы с живым предпросмотром.</summary>
public partial class ThemeEditorDialog : Window
{
    private sealed class Field
    {
        public required string Key { get; init; }
        public required string Label { get; init; }
        public required string Hint { get; init; }
        public required TextBox Box { get; init; }
        public required Border Swatch { get; init; }
    }

    private readonly List<Field> _fields = new();
    private readonly Color _accent;

    public ThemePreset? Result { get; private set; }

    public ThemeEditorDialog(ThemePreset? existing, Color accent)
    {
        InitializeComponent();

        _accent = accent;

        var start = existing ?? ThemeService.Presets[0];
        BuildFields(start);

        CbBase.ItemsSource = ThemeService.Presets.Select(p => p.Name).ToList();
        ChkLight.IsChecked = start.IsLight;

        UpdatePreview();
    }

    private void BuildFields(ThemePreset src)
    {
        PanelFields.Children.Clear();
        _fields.Clear();

        var defs = new (string Key, string Label, string Hint, string Value)[]
        {
            ("BgDeep", "Фон окна", "самый тёмный слой", src.BgDeep),
            ("Bg", "Фон панелей", "боковое меню, нижняя панель", src.Bg),
            ("Panel", "Карточки", "блоки настроек, списки", src.Panel),
            ("PanelHover", "Наведение", "подсветка под курсором", src.PanelHover),
            ("Border", "Границы", "рамки карточек", src.Border),
            ("Text", "Текст", "основной цвет букв", src.Text),
            ("TextMuted", "Текст блёклый", "подписи и пояснения", src.TextMuted)
        };

        foreach (var d in defs)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labelPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labelPanel.Children.Add(new TextBlock
            {
                Text = d.Label,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("Fg")
            });
            labelPanel.Children.Add(new TextBlock
            {
                Text = d.Hint,
                FontSize = 10,
                Foreground = (Brush)FindResource("FgMuted"),
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(labelPanel, 0);
            grid.Children.Add(labelPanel);

            var box = new TextBox { Text = d.Value, FontFamily = new FontFamily("Consolas") };
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);

            var swatch = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(8, 0, 0, 0),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("BorderBrushDark")
            };
            Grid.SetColumn(swatch, 2);
            grid.Children.Add(swatch);

            var field = new Field
            {
                Key = d.Key, Label = d.Label, Hint = d.Hint, Box = box, Swatch = swatch
            };

            box.TextChanged += (_, _) => { UpdateSwatch(field); UpdatePreview(); };
            _fields.Add(field);

            UpdateSwatch(field);
            PanelFields.Children.Add(grid);
        }
    }

    private static bool TryColor(string hex, out Color color)
    {
        color = Colors.Black;
        try
        {
            var s = hex.Trim();
            if (s.Length == 0) return false;
            if (!s.StartsWith('#')) s = "#" + s;
            if (s.Length is not (7 or 9 or 4)) return false;

            color = (Color)ColorConverter.ConvertFromString(s);
            return true;
        }
        catch { return false; }
    }

    private void UpdateSwatch(Field f)
    {
        if (TryColor(f.Box.Text, out var c))
        {
            f.Swatch.Background = new SolidColorBrush(c);
            f.Box.BorderBrush = (Brush)FindResource("BorderBrushDark");
        }
        else
        {
            f.Swatch.Background = Brushes.Transparent;
            f.Box.BorderBrush = (Brush)FindResource("Danger");
        }
    }

    private string Get(string key) =>
        _fields.First(f => f.Key == key).Box.Text.Trim();

    private void UpdatePreview()
    {
        try
        {
            if (!TryColor(Get("BgDeep"), out var bgDeep)) return;
            if (!TryColor(Get("Panel"), out var panel)) return;
            if (!TryColor(Get("Border"), out var border)) return;
            if (!TryColor(Get("Text"), out var text)) return;
            if (!TryColor(Get("TextMuted"), out var muted)) return;

            var light = ChkLight.IsChecked == true;

            PreviewRoot.Background = new SolidColorBrush(bgDeep);
            PreviewRoot.BorderBrush = new SolidColorBrush(border);

            PreviewPanel.Background = new SolidColorBrush(panel);
            PreviewTitle.Foreground = new SolidColorBrush(text);
            PreviewMuted.Foreground = new SolidColorBrush(muted);

            PreviewAccent.Background = new SolidColorBrush(_accent);
            PreviewAccentText.Foreground = new SolidColorBrush(
                light ? Colors.White : (Color)ColorConverter.ConvertFromString("#08130C"));

            PreviewCard.Background = new SolidColorBrush(panel);
            PreviewCard.BorderBrush = new SolidColorBrush(border);
            PreviewCardText.Foreground = new SolidColorBrush(text);

            TxtStatus.Text = "";
        }
        catch { /* предпросмотр не критичен */ }
    }

    private void CbBase_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || CbBase.SelectedItem is not string name) return;

        var preset = ThemeService.Presets.FirstOrDefault(p => p.Name == name);
        if (preset is null) return;

        BuildFields(preset);
        ChkLight.IsChecked = preset.IsLight;
        UpdatePreview();
    }

    private void Light_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdatePreview();
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        var bad = _fields.FirstOrDefault(f => !TryColor(f.Box.Text, out _));
        if (bad is not null)
        {
            TxtStatus.Text = $"Некорректный цвет в поле «{bad.Label}». Формат: #1B1F26";
            bad.Box.Focus();
            return;
        }

        string Norm(string key)
        {
            var v = Get(key);
            return v.StartsWith('#') ? v : "#" + v;
        }

        Result = new ThemePreset
        {
            Name = ThemeService.CustomThemeName,
            BgDeep = Norm("BgDeep"),
            Bg = Norm("Bg"),
            Panel = Norm("Panel"),
            PanelHover = Norm("PanelHover"),
            Border = Norm("Border"),
            Text = Norm("Text"),
            TextMuted = Norm("TextMuted"),
            IsLight = ChkLight.IsChecked == true
        };

        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }
}
