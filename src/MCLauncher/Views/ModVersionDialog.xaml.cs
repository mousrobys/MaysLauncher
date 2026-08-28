using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MCLauncher.Models;
using MCLauncher.Services;

namespace MCLauncher.Views;

/// <summary>
/// Выбор версии мода перед установкой — по образцу Modrinth App.
/// Показывает все версии проекта и позволяет выбрать целевую версию игры
/// и загрузчик, а не только те, что стоят в текущей сборке.
/// </summary>
public partial class ModVersionDialog : Window
{
    private readonly ModService _mods;
    private readonly ModSearchResult _project;
    private string _instanceMcVersion = "";
    private LoaderKind _instanceLoader = LoaderKind.Vanilla;

    private List<ModFile> _all = new();
    private List<ModFile> _shown = new();
    private CancellationTokenSource? _cts;

    private List<GameInstance> _instances = new();
    private GameInstance _currentInstance = null!;

    private const string AnyValue = "\u0000any";

    /// <summary>Выбранный файл (null, если отменено).</summary>
    public ModFile? SelectedFile { get; private set; }
    public bool InstallDependencies => ChkDependencies.IsChecked == true;

    /// <summary>Версия игры, под которую пользователь ставит мод.</summary>
    public string TargetMcVersion { get; private set; }

    /// <summary>Сборка (инстанс) лаунчера, куда устанавливается мод.</summary>
    public GameInstance TargetInstance => _currentInstance;

    public ModVersionDialog(ModService mods, ModSearchResult project, List<GameInstance> instances, GameInstance current)
    {
        InitializeComponent();

        _mods = mods;
        _project = project;
        _instances = instances;
        _currentInstance = current;
        _instanceMcVersion = current.McVersion;
        _instanceLoader = current.Loader;
        TargetMcVersion = current.McVersion;

        TxtTitle.Text = project.Title;
        TxtSubtitle.Text = $"{project.ProviderDisplay} · {project.DownloadsDisplay} загрузок";
        TxtInitial.Text = project.Title.Length > 0 ? project.Title[..1].ToUpperInvariant() : "?";

        // Список установленных в лаунчере сборок — куда можно поставить мод
        var items = _instances.Select(i => new ComboBoxItem
        {
            Content = string.IsNullOrWhiteSpace(i.McVersion)
                ? $"{i.Name} (версия не задана)"
                : $"{i.McVersion} · {i.Loader.Display()} — {i.Name}",
            Tag = i
        }).ToList();
        CbInstance.ItemsSource = items;
        CbInstance.SelectedItem = items.FirstOrDefault(x =>
            x.Tag is GameInstance g && g.Id == current.Id) ?? items.FirstOrDefault();

        Loaded += OnLoadedAsync;
        Closed += (_, _) => _cts?.Cancel();
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();

        // Иконку грузим асинхронно — раньше это вешало окно
        _ = LoadIconAsync(_cts.Token);

        await LoadVersionsAsync(_cts.Token);
    }

    private async Task LoadIconAsync(CancellationToken ct)
    {
        var img = await ImageCacheService.GetAsync(_project.IconUrl, App.Http, 104, ct);
        if (img is null || ct.IsCancellationRequested) return;

        Dispatcher.Invoke(() =>
        {
            ImgIcon.Source = img;
            TxtInitial.Visibility = Visibility.Collapsed;
        });
    }

    private async Task LoadVersionsAsync(CancellationToken ct)
    {
        TxtEmpty.Text = "Загружаю список версий…";
        TxtEmpty.Visibility = Visibility.Visible;

        try
        {
            _all = await _mods.GetAllFilesAsync(_project, ct);

            if (ct.IsCancellationRequested) return;

            if (_all.Count == 0)
            {
                TxtEmpty.Text = "Не удалось получить список версий этого мода.\n" +
                                "Проверьте подключение к интернету.";
                return;
            }

            BuildFilters();
            ApplyFilter();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            TxtEmpty.Text = "Ошибка: " + ex.Message;
            Log.Warn("Загрузка версий мода: " + ex.Message);
        }
    }

    /// <summary>Заполняет выпадающие списки тем, что реально есть у мода.</summary>
    private void BuildFilters()
    {
        // Версии игры, отсортированные от новых к старым
        var versions = _all
            .SelectMany(f => f.GameVersions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(v => VersionService.ParseMcVersion(v) ?? new Version(0, 0))
            .ThenByDescending(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var versionItems = new List<ComboBoxItem>
        {
            new() { Content = "Любая версия", Tag = AnyValue }
        };
        versionItems.AddRange(versions.Select(v => new ComboBoxItem { Content = v, Tag = v }));

        CbGameVersion.ItemsSource = versionItems;

        // По умолчанию — версия текущей сборки, если мод её поддерживает
        var preferred = versionItems.FirstOrDefault(i =>
            string.Equals(i.Tag as string, _instanceMcVersion, StringComparison.OrdinalIgnoreCase));

        CbGameVersion.SelectedItem = preferred ?? versionItems[0];

        // Загрузчики
        var loaders = _all
            .SelectMany(f => f.Loaders)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var loaderItems = new List<ComboBoxItem>
        {
            new() { Content = "Любой", Tag = AnyValue }
        };
        loaderItems.AddRange(loaders.Select(l => new ComboBoxItem
        {
            Content = char.ToUpperInvariant(l[0]) + l[1..],
            Tag = l
        }));

        CbLoader.ItemsSource = loaderItems;

        var wantLoader = _instanceLoader switch
        {
            LoaderKind.Fabric => "fabric",
            LoaderKind.Forge => "forge",
            LoaderKind.NeoForge => "neoforge",
            _ => null
        };

        var preferredLoader = wantLoader is null
            ? loaderItems[0]
            : loaderItems.FirstOrDefault(i =>
                  string.Equals(i.Tag as string, wantLoader, StringComparison.OrdinalIgnoreCase))
              ?? loaderItems[0];

        CbLoader.SelectedItem = preferredLoader;
    }

    private string SelectedGameVersion =>
        (CbGameVersion.SelectedItem as ComboBoxItem)?.Tag as string ?? AnyValue;

    private string SelectedLoader =>
        (CbLoader.SelectedItem as ComboBoxItem)?.Tag as string ?? AnyValue;

    private bool OnlyRelease =>
        (CbChannel.SelectedItem as ComboBoxItem)?.Tag as string == "release";

    private void ApplyFilter()
    {
        var gv = SelectedGameVersion;
        var ld = SelectedLoader;

        _shown = _all.Where(f =>
        {
            if (gv != AnyValue && f.GameVersions.Count > 0 &&
                !f.GameVersions.Contains(gv, StringComparer.OrdinalIgnoreCase)) return false;

            if (ld != AnyValue && f.Loaders.Count > 0 &&
                !f.Loaders.Contains(ld, StringComparer.OrdinalIgnoreCase)) return false;

            if (OnlyRelease && !f.ReleaseType.Equals("release", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }).ToList();

        TargetMcVersion = gv == AnyValue ? _instanceMcVersion : gv;

        // Подсказка о несовпадении со сборкой
        if (gv != AnyValue && !string.Equals(gv, _instanceMcVersion, StringComparison.OrdinalIgnoreCase))
        {
            TxtMatchInfo.Text = $"Сборка использует {_instanceMcVersion}. " +
                                "Мод для другой версии может не запуститься.";
            TxtMatchInfo.Foreground = (Brush)FindResource("Danger");
        }
        else
        {
            TxtMatchInfo.Text = $"Сборка: {_instanceMcVersion} · {_instanceLoader.Display()}";
            TxtMatchInfo.Foreground = (Brush)FindResource("FgMuted");
        }

        if (_shown.Count == 0)
        {
            LstVersions.ItemsSource = null;
            TxtEmpty.Text = $"У «{_project.Title}» нет версий с такими условиями.\n\n" +
                            "Попробуйте выбрать «Любая версия» или другой загрузчик.";
            TxtEmpty.Visibility = Visibility.Visible;
            BtnInstall.IsEnabled = false;
            return;
        }

        TxtEmpty.Visibility = Visibility.Collapsed;
        LstVersions.ItemsSource = _shown.Select(BuildRow).ToList();

        var best = _shown.FirstOrDefault(f =>
                       f.ReleaseType.Equals("release", StringComparison.OrdinalIgnoreCase))
                   ?? _shown[0];

        LstVersions.SelectedIndex = _shown.IndexOf(best);
    }

    /// <summary>Совместим ли файл именно с текущей сборкой.</summary>
    private bool MatchesInstance(ModFile f)
    {
        var versionOk = f.GameVersions.Count == 0 ||
                        f.GameVersions.Contains(_instanceMcVersion, StringComparer.OrdinalIgnoreCase);
        if (!versionOk) return false;

        if (f.Loaders.Count == 0 || _instanceLoader == LoaderKind.Vanilla) return true;

        var want = _instanceLoader switch
        {
            LoaderKind.Fabric => "fabric",
            LoaderKind.Forge => "forge",
            LoaderKind.NeoForge => "neoforge",
            _ => ""
        };

        return f.Loaders.Any(l => l.Equals(want, StringComparison.OrdinalIgnoreCase));
    }

    private object BuildRow(ModFile f)
    {
        var compatible = MatchesInstance(f);

        var (typeText, typeBg, typeFg) = f.ReleaseType.ToLowerInvariant() switch
        {
            "release" => ("RELEASE", "#2A1A40", "#A855F7"),
            "beta" => ("BETA", "#33280F", "#FACC15"),
            _ => ("ALPHA", "#331A1A", "#F87171")
        };

        var versions = f.GameVersions.Count > 0
            ? string.Join(", ", f.GameVersions.Take(4)) + (f.GameVersions.Count > 4 ? "…" : "")
            : "любая версия";

        var loaders = f.Loaders.Count > 0 ? " · " + string.Join(", ", f.Loaders.Take(3)) : "";
        var date = f.Published?.ToLocalTime().ToString("dd.MM.yyyy") ?? "";

        return new
        {
            Name = string.IsNullOrWhiteSpace(f.DisplayName) ? f.FileName : f.DisplayName,
            Details = $"{versions}{loaders}" + (date.Length > 0 ? $" · {date}" : ""),
            SizeText = f.SizeDisplay,
            TypeText = typeText,
            TypeBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(typeBg)),
            TypeFg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(typeFg)),
            CompatText = compatible ? "подходит сборке" : "другая версия",
            CompatColor = new SolidColorBrush(compatible
                ? ThemeService.CurrentAccent
                : (Color)ColorConverter.ConvertFromString("#F87171")),
            RowBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                ThemeService.CurrentTheme.IsLight ? "#F3F5F8" : "#20252E")),
            RowBorder = new SolidColorBrush(compatible
                ? (Color)ColorConverter.ConvertFromString("#00000000")
                : (Color)ColorConverter.ConvertFromString("#3A2428"))
        };
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _all.Count == 0) return;
        ApplyFilter();
    }

    private void Instance_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _instances.Count == 0) return;
        if (CbInstance.SelectedItem is not ComboBoxItem item || item.Tag is not GameInstance sel) return;
        if (sel.Id == _currentInstance.Id) return;

        _currentInstance = sel;
        _instanceMcVersion = sel.McVersion;
        _instanceLoader = sel.Loader;
        BuildFilters();
        ApplyFilter();
    }

    private void LstVersions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = LstVersions.SelectedIndex;
        if (idx < 0 || idx >= _shown.Count)
        {
            BtnInstall.IsEnabled = false;
            TxtDepInfo.Text = "";
            return;
        }

        var f = _shown[idx];
        BtnInstall.IsEnabled = true;

        var required = f.Dependencies.Count(d => d.IsRequired);
        TxtDepInfo.Text = required > 0
            ? $"Обязательных зависимостей: {required} — установятся автоматически."
            : "Дополнительных зависимостей нет.";

        BtnInstall.Content = MatchesInstance(f) ? "Установить" : "Установить всё равно";
    }

    private void BtnInstall_Click(object sender, RoutedEventArgs e)
    {
        var idx = LstVersions.SelectedIndex;
        if (idx < 0 || idx >= _shown.Count) return;

        var file = _shown[idx];

        if (!MatchesInstance(file))
        {
            var r = MessageBox.Show(
                $"«{file.DisplayName}» не заявлена как совместимая с " +
                $"Minecraft {_instanceMcVersion} ({_instanceLoader.Display()}).\n\n" +
                "Игра может не запуститься. Всё равно установить?",
                "Несовместимая версия", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (r != MessageBoxResult.Yes) return;
        }

        SelectedFile = file;
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
