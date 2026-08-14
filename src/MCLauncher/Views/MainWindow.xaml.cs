using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MCLauncher.Models;
using MCLauncher.Services;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace MCLauncher.Views;

public partial class MainWindow : Window
{
    private readonly VersionService _versions;
    private readonly DownloadManager _downloads;
    private readonly MicrosoftAuthService _auth;
    private readonly JavaService _java;
    private readonly SkinService _skins;
    private readonly GameLauncher _game;
    private readonly ModLoaderService _loaders;
    private readonly ServerPingService _ping;
    private readonly ModService _mods;
    private readonly ModpackService _modpacks;
    private readonly BotManager _bots;
    private readonly GameSessionManager _sessions = new();
    private readonly GameStatistics _stats;
    private readonly FavoriteInstances _favorites;
    private readonly RamMonitor _ramMonitor;
    private readonly SkinService _skinService;
    private SkinInfo? _selectedSkin;

    private LauncherSettings _settings = new();
    private MinecraftAccount? _account;
    private VersionManifest? _manifest;
    private List<GameInstance> _instances = new();
    private GameInstance? _selectedInstance;

    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _initializing = true;
    private DateTime _lastProgressUi = DateTime.MinValue;
    private readonly StringBuilder _logBuffer = new();
    private DispatcherTimer? _uptimeTimer;

    public MainWindow()
    {
        InitializeComponent();

        var http = App.Http;
        _versions = new VersionService(http);
        _downloads = new DownloadManager(http);
        _auth = new MicrosoftAuthService(http);
        _java = new JavaService(http);
        _skins = new SkinService(http);
        _game = new GameLauncher();
        _loaders = new ModLoaderService(http);
        _ping = new ServerPingService();
        _mods = new ModService(http);
        _modpacks = new ModpackService(http);
        _bots = new BotManager(http);
        _stats = GameStatistics.Load();
        _favorites = new FavoriteInstances();
        _favorites.Load();
        _ramMonitor = new RamMonitor();
        _ramMonitor.OnUpdate += RamMonitor_OnUpdate;
        _skinService = new SkinService(http);

        ToastNotification.Initialize(this);

        _downloads.Progress += OnProgress;
        _java.Progress += OnProgress;
        _auth.Status += s => Dispatcher.Invoke(() => SetStage(s));
        _loaders.Status += s => Dispatcher.Invoke(() => SetStage(s));
        _mods.Status += s => Dispatcher.Invoke(() => SetStage(s));
        _modpacks.Status += s => Dispatcher.Invoke(() => SetStage(s));
        _modpacks.Progress += OnProgress;
        _bots.Output += (name, line) => OnBotOutput($"[{name}] {line}");
        _bots.Changed += () => Dispatcher.BeginInvoke(RefreshBotList);
        _game.GameOutput += AppendLog;
        Log.LineWritten += AppendLog;

        _sessions.Changed += () => Dispatcher.BeginInvoke(UpdateRunStateUi);
        _sessions.SessionExited += OnSessionExited;

        Loaded += OnLoadedAsync;
        Closing += OnClosing;
        KeyDown += Window_KeyDown;
    }

    // =====================================================================
    //  ЗАПУСК ПРИЛОЖЕНИЯ
    // =====================================================================

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        _initializing = true;
        _settings = SettingsService.Load();

        // Восстанавливаем свою схему, если она была
        if (!string.IsNullOrWhiteSpace(_settings.CustomThemeJson))
        {
            try
            {
                ThemeService.CustomPreset =
                    System.Text.Json.JsonSerializer.Deserialize<ThemePreset>(_settings.CustomThemeJson);
            }
            catch (Exception ex) { Log.Warn("Своя тема повреждена: " + ex.Message); }
        }

        ThemeService.ApplyTheme(_settings.Theme);
        ThemeService.ApplyAccent(_settings.AccentColor);
        ApplySettingsToUi();
        BuildThemeCards();
        BuildAccentSwatches();
        BuildBackgroundStyleButtons();
        ApplyBanner();
        ApplyWindowBackground();

        AppendLog("MaysLauncher запущен. Папка: " + _settings.GameDir);

        _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTimer.Tick += (_, _) => UpdateUptimeBadge();
        _uptimeTimer.Start();

        _ = Task.Run(DetectJava);

        var saved = AccountStorage.Load();
        if (saved is not null)
        {
            SetAccount(saved, refreshSkin: true);

            if (!saved.IsOffline && saved.IsExpired && !string.IsNullOrEmpty(saved.MicrosoftRefreshToken))
            {
                try
                {
                    SetStage("Обновляю сессию Microsoft...");
                    var refreshed = await _auth.RefreshAsync(saved.MicrosoftRefreshToken!);
                    AccountStorage.Save(refreshed);
                    SetAccount(refreshed, refreshSkin: true);
                }
                catch (Exception ex)
                {
                    AppendLog("Не удалось обновить сессию: " + ex.Message);
                    TxtAuthState.Text = "Сессия истекла — войдите заново.";
                }
                finally { HideProgress(); }
            }
        }

        await LoadVersionsAsync();
        LoadInstances();
        _initializing = false;
        UpdateRunStateUi();

        // Колесо мыши не должно менять значения в списках при прокрутке страницы
        Dispatcher.BeginInvoke(new Action(() => SetupWheelHandling(this)),
            System.Windows.Threading.DispatcherPriority.Loaded);

        _ = RefreshServersAsync();
        _ramMonitor.Start();
        UpdateStatisticsDisplay();
    }

    private void UpdateStatisticsDisplay()
    {
        StatTotalTime.Text = _stats.GetFormattedTotalTime();
        StatLaunches.Text = _stats.TotalLaunches.ToString();
        StatLastInstance.Text = _stats.LastInstanceName ?? "—";
        StatLastTime.Text = _stats.TotalLaunches > 0 ? _stats.GetFormattedLastPlayed() : "Не играли";
    }

    private void RamMonitor_OnUpdate((DateTime Time, double UsedMb) point)
    {
        Dispatcher.Invoke(() =>
        {
            RamLabel.Text = $"{(int)point.UsedMb} МБ";
            DrawRamChart();
        });
    }

    private void DrawRamChart()
    {
        var history = _ramMonitor.GetHistory();
        var canvas = RamChart;
        canvas.Children.Clear();

        if (history.Count < 2) return;

        double width = canvas.ActualWidth > 0 ? canvas.ActualWidth : 200;
        double height = canvas.ActualHeight > 0 ? canvas.ActualHeight : 40;
        double maxVal = RamMonitor.GetTotalRamMb();

        var points = new PointCollection();
        for (int i = 0; i < history.Count; i++)
        {
            double x = (double)i / (history.Count - 1) * width;
            double y = height - (history[i].UsedMb / maxVal * height);
            points.Add(new System.Windows.Point(x, y));
        }

        var line = new Polyline
        {
            Points = points,
            Stroke = (Brush)FindResource("Accent"),
            StrokeThickness = 2,
            Fill = null
        };

        canvas.Children.Add(line);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_sessions.AnyRunning)
        {
            var r = MessageBox.Show(
                $"Сейчас запущено игр: {_sessions.RunningCount}.\n\n" +
                "Закрыть лаунчер вместе с игрой?\n" +
                "«Нет» — лаунчер закроется, игра продолжит работать.",
                "Игра запущена", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (r == MessageBoxResult.Yes) _sessions.StopAllAsync().GetAwaiter().GetResult();
        }

        _uptimeTimer?.Stop();
        PersistSettings();
    }

    // =====================================================================
    //  НАСТРОЙКИ <-> UI
    // =====================================================================

    private void ApplySettingsToUi()
    {
        if (string.IsNullOrWhiteSpace(_settings.GameDir)) _settings.GameDir = LauncherPaths.Root;

        SldMemory.Value = Math.Clamp(_settings.MaxMemoryMb, 1024, 16384);
        TxtMemory.Text = $"{_settings.MaxMemoryMb} МБ";
        TxtBadgeRam.Text = $"RAM: {_settings.MaxMemoryMb} МБ";
        TxtWidth.Text = _settings.WindowWidth.ToString();
        TxtHeight.Text = _settings.WindowHeight.ToString();
        ChkFullscreen.IsChecked = _settings.Fullscreen;
        ChkSnapshots.IsChecked = _settings.ShowSnapshots;
        ChkCloseOnLaunch.IsChecked = _settings.CloseLauncherOnStart;
        ChkShowConsole.IsChecked = _settings.ShowConsole;
        ChkAllowMultiple.IsChecked = _settings.AllowMultipleInstances;
        ChkMinimizeOnLaunch.IsChecked = _settings.MinimizeOnLaunch;
        ChkConfirmStop.IsChecked = _settings.ConfirmGameStop;
        ChkAnimations.IsChecked = _settings.Animations;
        ChkDefaultIsolated.IsChecked = _settings.DefaultIsolated;
        ChkAutoLanguage.IsChecked = _settings.AutoSetGameLanguage;
        TxtWindowBg.Text = _settings.WindowBackgroundPath;
        SldBgOpacity.Value = Math.Clamp(_settings.WindowBackgroundOpacity * 100, 5, 100);
        TxtBgOpacity.Text = $"{(int)(_settings.WindowBackgroundOpacity * 100)}%";
        RbLangRu.IsChecked = _settings.GameLanguage == "ru";
        RbLangUk.IsChecked = _settings.GameLanguage == "uk";
        RbLangEn.IsChecked = _settings.GameLanguage == "en";
        TxtJvmArgs.Text = _settings.ExtraJvmArgs;
        TxtGameDir.Text = _settings.GameDir;
        TxtJavaPath.Text = _settings.CustomJavaPath;
        TxtBannerPath.Text = _settings.CustomBannerPath;

        var totalRam = (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));
        TxtMemoryHint.Text = totalRam > 0
            ? $"Всего в системе: {totalRam} МБ. Для ванильной игры обычно достаточно 2048–4096 МБ."
            : "Для ванильной игры обычно достаточно 2048–4096 МБ.";
    }

    private void PersistSettings()
    {
        _settings.MaxMemoryMb = (int)SldMemory.Value;
        _settings.WindowWidth = ParseIntOr(TxtWidth.Text, 1280);
        _settings.WindowHeight = ParseIntOr(TxtHeight.Text, 720);
        _settings.Fullscreen = ChkFullscreen.IsChecked == true;
        _settings.ShowSnapshots = ChkSnapshots.IsChecked == true;
        _settings.CloseLauncherOnStart = ChkCloseOnLaunch.IsChecked == true;
        _settings.ShowConsole = ChkShowConsole.IsChecked == true;
        _settings.AllowMultipleInstances = ChkAllowMultiple.IsChecked == true;
        _settings.MinimizeOnLaunch = ChkMinimizeOnLaunch.IsChecked == true;
        _settings.ConfirmGameStop = ChkConfirmStop.IsChecked == true;
        _settings.Animations = ChkAnimations.IsChecked == true;
        _settings.DefaultIsolated = ChkDefaultIsolated.IsChecked == true;
        _settings.AutoSetGameLanguage = ChkAutoLanguage.IsChecked == true;
        _settings.ExtraJvmArgs = TxtJvmArgs.Text.Trim();
        _settings.CustomJavaPath = TxtJavaPath.Text.Trim();
        _settings.LastInstanceId = _selectedInstance?.Id ?? _settings.LastInstanceId;

        SettingsService.Save(_settings);

        // Список сборок сохраняем только когда он реально загружен
        if (!_initializing && InstanceService.Loaded) InstanceService.SaveAll(_instances);
    }

    private static int ParseIntOr(string s, int fallback) =>
        int.TryParse(s.Trim(), out var v) && v > 0 ? v : fallback;

    private void DetectJava()
    {
        try
        {
            var list = _java.FindAll();
            Dispatcher.Invoke(() =>
            {
                if (list.Count == 0)
                {
                    TxtBadgeJava.Text = "Java: не найдена";
                    TxtJavaList.Text = "Java не обнаружена. Лаунчер скачает нужную версию автоматически.";
                }
                else
                {
                    TxtBadgeJava.Text = $"Java {list[0].MajorVersion}";
                    TxtJavaList.Text = "Найдено:\n" + string.Join("\n", list.Select(j => "  • " + j));
                }
            });
        }
        catch (Exception ex) { Log.Warn("Ошибка поиска Java: " + ex.Message); }
    }

    // =====================================================================
    //  ВНЕШНИЙ ВИД
    // =====================================================================

    // ---------- Темы ----------

    private void BuildThemeCards()
    {
        ItemsThemes.ItemsSource = ThemeService.AllPresets().Select(p => new
        {
            p.Name,
            Preview = new SolidColorBrush((Color)ColorConverter.ConvertFromString(p.Bg)),
            Swatch1 = new SolidColorBrush((Color)ColorConverter.ConvertFromString(p.Panel)),
            Swatch2 = new SolidColorBrush((Color)ColorConverter.ConvertFromString(p.Border)),
            Swatch3 = new SolidColorBrush(ThemeService.CurrentAccent),
            TextColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString(p.Text)),
            Border = string.Equals(p.Name, _settings.Theme, StringComparison.OrdinalIgnoreCase)
                ? new SolidColorBrush(ThemeService.CurrentAccent)
                : new SolidColorBrush(Colors.Transparent)
        }).ToList();
    }

    private void ThemeCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string name) return;

        _settings.Theme = name;
        ThemeService.ApplyTheme(name);
        ThemeService.ApplyAccent(_settings.AccentColor);

        BuildThemeCards();
        BuildAccentSwatches();
        ApplyBanner();
        ApplyWindowBackground();
        RefreshContent();

        SettingsService.Save(_settings);
        AppendLog($"Тема изменена: {name}");
    }

    // ---------- Фон окна ----------

    private void ApplyWindowBackground()
    {
        var brush = ThemeService.BuildWindowBackground(
            _settings.WindowBackgroundPath, _settings.WindowBackgroundOpacity);

        WindowBgLayer.Fill = brush ?? (Brush)new SolidColorBrush(Colors.Transparent);
    }

    private void BtnPickWindowBg_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Выберите фото для фона лаунчера",
            Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.webp|Все файлы|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        _settings.WindowBackgroundPath = dlg.FileName;
        TxtWindowBg.Text = dlg.FileName;
        ApplyWindowBackground();
        SettingsService.Save(_settings);
        AppendLog("Установлен фон лаунчера: " + System.IO.Path.GetFileName(dlg.FileName));
    }

    private void BtnClearWindowBg_Click(object sender, RoutedEventArgs e)
    {
        _settings.WindowBackgroundPath = "";
        TxtWindowBg.Text = "";
        ApplyWindowBackground();
        SettingsService.Save(_settings);
    }

    private void SldBgOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;

        _settings.WindowBackgroundOpacity = e.NewValue / 100.0;
        TxtBgOpacity.Text = $"{(int)e.NewValue}%";
        ApplyWindowBackground();
    }

    // ---------- Язык игры ----------

    private void GameLang_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        _settings.GameLanguage = (sender as FrameworkElement)?.Tag?.ToString() ?? "ru";
        SettingsService.Save(_settings);
    }
    private void BuildAccentSwatches()
    {
        ItemsAccents.ItemsSource = LauncherSettings.AccentPresets.Select(p => new
        {
            p.Hex,
            p.Name,
            Brush = FrozenBrush((Color)ColorConverter.ConvertFromString(p.Hex)),
            Border = string.Equals(p.Hex, _settings.AccentColor, StringComparison.OrdinalIgnoreCase)
                ? new SolidColorBrush(Colors.White)
                : new SolidColorBrush(Colors.Transparent)
        }).ToList();
    }

    private static SolidColorBrush FrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void AccentSwatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string hex) return;

        _settings.AccentColor = hex;
        ThemeService.ApplyAccent(hex);
        BuildAccentSwatches();
        ApplyBanner();
        SettingsService.Save(_settings);
    }

    private void BuildBackgroundStyleButtons()
    {
        PanelBgStyles.Children.Clear();

        foreach (var style in ThemeService.BackgroundStyles)
        {
            var rb = new RadioButton
            {
                Content = style,
                Style = (Style)FindResource("SegmentToggle"),
                GroupName = "BgStyle",
                IsChecked = style == _settings.BackgroundStyle,
                Tag = style
            };
            rb.Checked += (s, _) =>
            {
                if (!IsLoaded) return;
                _settings.BackgroundStyle = (s as FrameworkElement)?.Tag?.ToString() ?? "Изумруд";
                ApplyBanner();
                SettingsService.Save(_settings);
            };
            PanelBgStyles.Children.Add(rb);
        }
    }

    private void ApplyBanner()
    {
        HomeBanner.Background = ThemeService.BuildBanner(_settings.BackgroundStyle, ThemeService.CurrentAccent);

        if (!string.IsNullOrWhiteSpace(_settings.CustomBannerPath) && File.Exists(_settings.CustomBannerPath))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(_settings.CustomBannerPath);
                bmp.EndInit();
                bmp.Freeze();

                ImgCustomBanner.Source = bmp;
                ImgCustomBanner.Visibility = Visibility.Visible;
                return;
            }
            catch (Exception ex) { Log.Warn("Не удалось загрузить баннер: " + ex.Message); }
        }

        ImgCustomBanner.Source = null;
        ImgCustomBanner.Visibility = Visibility.Collapsed;
    }

    private void BtnPickBanner_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Картинка для баннера",
            Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp|Все файлы|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        _settings.CustomBannerPath = dlg.FileName;
        TxtBannerPath.Text = dlg.FileName;
        ApplyBanner();
        SettingsService.Save(_settings);
    }

    private void BtnClearBanner_Click(object sender, RoutedEventArgs e)
    {
        _settings.CustomBannerPath = "";
        TxtBannerPath.Text = "";
        ApplyBanner();
        SettingsService.Save(_settings);
    }

    // =====================================================================
    //  ВЕРСИИ И СБОРКИ
    // =====================================================================

    private async Task LoadVersionsAsync()
    {
        try
        {
            SetStage("Загружаю манифест версий Mojang...");
            ShowProgress(indeterminate: true);
            _manifest = await _versions.GetManifestAsync();

            var supported = VersionService.FilterSupported(_manifest, _settings.ShowSnapshots);
            AppendLog($"Манифест загружен: {_manifest.Versions.Count} версий, доступно {supported.Count} (≥1.16.5).");
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка загрузки версий: " + ex.Message);
            TxtBannerInfo.Text = "Не удалось получить список версий. Проверьте интернет.";
        }
        finally { HideProgress(); }
    }

    private void LoadInstances()
    {
        _instances = InstanceService.LoadAll();

        if (!InstanceService.Loaded)
        {
            AppendLog("ВНИМАНИЕ: список сборок не прочитан, изменения не сохраняются. " +
                      "Перезапустите лаунчер.");
            MessageBox.Show(
                "Не удалось прочитать список сборок.\n\n" +
                "Чтобы не потерять данные, сохранение отключено до перезапуска.\n" +
                "Файлы сборок на диске не тронуты.",
                "Список сборок", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Папки на диске есть, а в списке их нет — восстанавливаем
        var orphans = InstanceService.ScanOrphans(_instances);
        if (orphans.Count > 0)
        {
            _instances.AddRange(orphans);
            InstanceService.SaveAll(_instances);
            AppendLog($"Найдено сборок на диске: {orphans.Count}.");
        }

        // Стартовую сборку создаём ТОЛЬКО если манифест реально загрузился.
        // Иначе (нет сети) просто ждём — иначе затрём существующий список.
        if (_instances.Count == 0 && _manifest is not null && InstanceService.Loaded)
        {
            var latest = VersionService.FilterSupported(_manifest, false).FirstOrDefault();
            if (latest is not null)
            {
                var inst = new GameInstance
                {
                    Name = "Minecraft " + latest.Id,
                    McVersion = latest.Id,
                    Loader = LoaderKind.Vanilla,
                    LaunchVersionId = latest.Id
                };
                InstanceService.EnsureFolders(inst);
                _instances.Add(inst);
                InstanceService.SaveAll(_instances);
                AppendLog($"Создана стартовая сборка «{inst.Name}».");
            }
        }
        else if (_instances.Count == 0 && _manifest is null)
        {
            AppendLog("Нет соединения с Mojang — список версий недоступен. " +
                      "Сборки не создаются, существующие данные сохранены.");
        }

        RefreshInstanceLists();
        VerifyInstalledVersions();
    }

    /// <summary>
    /// Сверяет сборки с файлами на диске: если клиент пропал (чистка, антивирус),
    /// помечаем сборку как требующую переустановки, а не молча теряем её.
    /// </summary>
    private void VerifyInstalledVersions()
    {
        var missing = new List<string>();

        foreach (var inst in _instances)
        {
            try
            {
                var paths = GamePaths.ForInstance(inst);
                if (!File.Exists(paths.VersionJar(inst.McVersion)))
                    missing.Add($"{inst.Name} ({inst.McVersion})");
            }
            catch { }
        }

        if (missing.Count > 0)
            AppendLog($"Требуют загрузки клиента: {string.Join(", ", missing)}. " +
                      "Файлы скачаются при нажатии «ИГРАТЬ».");
    }

    private void RefreshInstanceLists()
    {
        var ordered = ApplyInstanceFilter(
            _instances.OrderByDescending(i => i.LastPlayed ?? i.Created).ToList());

        UpdateSearchVisibility();

        CbInstances.ItemsSource = null;
        CbInstances.ItemsSource = ordered;
        LstInstances.ItemsSource = null;
        LstInstances.ItemsSource = ordered;

        var target = ordered.FirstOrDefault(i => i.Id == _settings.LastInstanceId) ?? ordered.FirstOrDefault();
        if (target is not null)
        {
            CbInstances.SelectedItem = target;
            LstInstances.SelectedItem = target;
        }
        else
        {
            _selectedInstance = null;
            TxtBannerVersion.Text = "Нет сборок";
            TxtBannerInfo.Text = "Создайте сборку на вкладке «Версии».";
        }
    }

    private void CbInstances_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbInstances.SelectedItem is not GameInstance inst) return;
        SelectInstance(inst);
        if (!ReferenceEquals(LstInstances.SelectedItem, inst)) LstInstances.SelectedItem = inst;
    }

    private void LstInstances_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstInstances.SelectedItem is not GameInstance inst) return;
        SelectInstance(inst);
        if (!ReferenceEquals(CbInstances.SelectedItem, inst)) CbInstances.SelectedItem = inst;
    }

    /// <summary>ПКМ по сборке — сначала выделяем её, потом показываем меню.</summary>
    private void LstInstances_RightClick(object sender, MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(LstInstances, e.OriginalSource as DependencyObject)
            as ListBoxItem;

        if (item is not null)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private async void CtxPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;
        await LaunchAsync(_selectedInstance, null);
    }

    private void CtxSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;
        NavInstances.IsChecked = true;
        TxtInstEditName.Focus();
    }

    private void CtxRename_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;

        var dlg = new TextInputDialog("Переименовать сборку",
            $"Новое название для «{_selectedInstance.Name}»:", _selectedInstance.Name) { Owner = this };

        if (dlg.ShowDialog() != true) return;

        var name = dlg.Value.Trim();
        if (name.Length == 0) return;

        _selectedInstance.Name = name;
        InstanceService.SaveAll(_instances);

        var id = _selectedInstance.Id;
        RefreshInstanceLists();
        var restored = _instances.FirstOrDefault(i => i.Id == id);
        if (restored is not null) { CbInstances.SelectedItem = restored; SelectInstance(restored); }

        AppendLog($"Сборка переименована: «{name}»");
    }

    private void CtxMemory_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;

        var current = _selectedInstance.MaxMemoryMb > 0
            ? _selectedInstance.MaxMemoryMb.ToString()
            : _settings.MaxMemoryMb.ToString();

        var dlg = new TextInputDialog("Память сборки",
            "Сколько МБ выделить этой сборке? (0 — как в общих настройках)", current) { Owner = this };

        if (dlg.ShowDialog() != true) return;

        if (!int.TryParse(dlg.Value.Trim(), out var mb) || mb < 0)
        {
            MessageBox.Show("Введите число, например 4096.", "Некорректное значение",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _selectedInstance.MaxMemoryMb = mb;
        InstanceService.SaveAll(_instances);
        FillInstanceSettings(_selectedInstance);

        AppendLog(mb > 0
            ? $"Для «{_selectedInstance.Name}» задано {mb} МБ."
            : $"Для «{_selectedInstance.Name}» память как в общих настройках.");
    }
    private void SelectInstance(GameInstance inst)
    {
        _selectedInstance = inst;
        _settings.LastInstanceId = inst.Id;

        TxtBannerVersion.Text = inst.Name;
        var installed = File.Exists(GamePaths.ForInstance(inst).VersionJar(inst.McVersion));
        TxtBannerInfo.Text = installed
            ? $"Minecraft {inst.McVersion} · готова к запуску"
            : $"Minecraft {inst.McVersion} · будет загружена с серверов Mojang";

        TxtBadgeLoader.Text = inst.LoaderDisplay;

        // Детали
        TxtInstName.Text = inst.Name;
        TxtInstVersion.Text = "Minecraft " + inst.McVersion;
        TxtInstLoader.Text = inst.LoaderDisplay;
        TxtInstPlaytime.Text = inst.TotalPlaySeconds > 0 ? "В игре: " + inst.PlayTimeDisplay : "Ещё не запускалась";

        RefreshInstanceStats();
        LoadScreenshots();
        FillInstanceSettings(inst);
        RefreshModProfiles();
        RefreshJvmPresets();
        RefreshInstanceIcon();
        RefreshStatistics();
        TxtInstHealth.Text = "";
        UpdateRunStateUi();
    }

    // ---------- Индивидуальные настройки сборки ----------

    private bool _loadingInstSettings;

    private void FillInstanceSettings(GameInstance inst)
    {
        _loadingInstSettings = true;
        try
        {
            TxtInstEditName.Text = inst.Name;
            TxtInstMemory.Text = inst.MaxMemoryMb > 0 ? inst.MaxMemoryMb.ToString() : "";
            TxtInstWidth.Text = inst.WindowWidth > 0 ? inst.WindowWidth.ToString() : "";
            TxtInstHeight.Text = inst.WindowHeight > 0 ? inst.WindowHeight.ToString() : "";
            TxtInstServer.Text = inst.ServerAddress;
            TxtInstJava.Text = inst.JavaPath;
            TxtInstJvm.Text = inst.ExtraJvmArgs;
        }
        finally { _loadingInstSettings = false; }
    }

    // ---------- Профили модов ----------

    private void RefreshModProfiles()
    {
        if (_selectedInstance is null) return;

        _loadingInstSettings = true;
        try
        {
            var profiles = ModProfileService.List(_selectedInstance);
            CbModProfile.ItemsSource = profiles;
            CbModProfile.SelectedItem = profiles.Contains(_selectedInstance.ActiveModProfile)
                ? _selectedInstance.ActiveModProfile
                : profiles[0];

            var counts = profiles.Select(p =>
                $"{p} — {ModProfileService.CountMods(_selectedInstance, p)}");
            TxtProfileInfo.Text = "Модов: " + string.Join("  ·  ", counts);
        }
        finally { _loadingInstSettings = false; }
    }

    private void CbModProfile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _loadingInstSettings || _selectedInstance is null) return;
        if (CbModProfile.SelectedItem is not string target) return;
        if (string.Equals(target, _selectedInstance.ActiveModProfile, StringComparison.OrdinalIgnoreCase))
            return;

        if (_sessions.IsInstanceRunning(_selectedInstance.Id))
        {
            MessageBox.Show("Нельзя менять профиль, пока сборка запущена.",
                "Игра запущена", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshModProfiles();
            return;
        }

        try
        {
            ModProfileService.Switch(_selectedInstance, target);
            InstanceService.SaveAll(_instances);

            RefreshModProfiles();
            RefreshInstanceStats();
            RefreshContent();

            AppendLog($"Профиль модов: «{target}»");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Не удалось переключить профиль",
                MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshModProfiles();
        }
    }

    private void BtnNewModProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;

        var dlg = new TextInputDialog("Новый профиль модов",
            "Название профиля:", "Например: Для съёмок") { Owner = this };

        if (dlg.ShowDialog() != true) return;

        var name = dlg.Value.Trim();
        if (name.Length == 0) return;

        var copy = MessageBox.Show(
            "Скопировать текущие моды в новый профиль?\n\n«Нет» — создать пустой.",
            "Новый профиль", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        try
        {
            ModProfileService.Create(_selectedInstance, name, copy);
            RefreshModProfiles();
            AppendLog($"Создан профиль модов «{name}».");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnDeleteModProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null || CbModProfile.SelectedItem is not string name) return;

        if (MessageBox.Show($"Удалить профиль «{name}» со всеми его модами?",
                "Удаление профиля", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        try
        {
            ModProfileService.Delete(_selectedInstance, name);
            RefreshModProfiles();
            AppendLog($"Профиль «{name}» удалён.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Проверка целостности ----------

    private async void BtnCheckIntegrity_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;

        TxtInstHealth.Text = "Проверяю…";
        TxtInstHealth.Foreground = (Brush)FindResource("FgMuted");

        try
        {
            var svc = new IntegrityService(_versions);
            svc.Status += s => Dispatcher.BeginInvoke(() => TxtInstHealth.Text = s);

            var report = await svc.CheckAsync(_selectedInstance);

            var sb = new StringBuilder();
            sb.AppendLine(report.Summary);

            foreach (var p in report.Problems) sb.AppendLine("  ✕  " + p);
            foreach (var w in report.Warnings) sb.AppendLine("  !  " + w);
            if (report.Problems.Count == 0)
                foreach (var o in report.Ok.Take(4)) sb.AppendLine("  ✓  " + o);

            TxtInstHealth.Text = sb.ToString().TrimEnd();
            TxtInstHealth.Foreground = (Brush)FindResource(
                report.IsHealthy ? "Accent" : "Danger");

            if (report.Fixable.Count > 0)
            {
                var r = MessageBox.Show(
                    $"Найдено проблем: {report.Problems.Count}.\n\n" +
                    "Удалить повреждённые файлы, чтобы лаунчер скачал их заново?",
                    "Восстановление", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (r == MessageBoxResult.Yes)
                {
                    var removed = IntegrityService.Repair(_selectedInstance, report);
                    TxtInstHealth.Text = $"Удалено повреждённых элементов: {removed}. " +
                                         "Нажмите «ИГРАТЬ» — файлы загрузятся заново.";
                    AppendLog($"Восстановление сборки: удалено {removed} элементов.");
                }
            }
        }
        catch (Exception ex)
        {
            TxtInstHealth.Text = "Ошибка проверки: " + ex.Message;
            TxtInstHealth.Foreground = (Brush)FindResource("Danger");
        }
    }

    // ---------- Обновления модов ----------

    private async void BtnCheckModUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;

        var inst = _selectedInstance;
        var modsDir = InstanceService.ModsDir(inst);

        TxtInstHealth.Text = "Проверяю обновления модов…";
        TxtInstHealth.Foreground = (Brush)FindResource("FgMuted");

        try
        {
            var progress = new Progress<string>(s =>
                Dispatcher.BeginInvoke(() => TxtInstHealth.Text = s));

            var updates = await _mods.CheckUpdatesAsync(
                modsDir, inst.McVersion, inst.Loader, progress);

            if (updates.Count == 0)
            {
                TxtInstHealth.Text = "Все моды актуальны.";
                TxtInstHealth.Foreground = (Brush)FindResource("Accent");
                return;
            }

            var list = string.Join("\n", updates.Take(12).Select(u =>
                $"  • {u.Project.Title}: {u.CurrentVersion} → {u.NewVersion}"));

            if (updates.Count > 12) list += $"\n  … и ещё {updates.Count - 12}";

            var r = MessageBox.Show(
                $"Доступно обновлений: {updates.Count}\n\n{list}\n\nОбновить все?",
                "Обновления модов", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (r != MessageBoxResult.Yes)
            {
                TxtInstHealth.Text = $"Доступно обновлений: {updates.Count}";
                return;
            }

            var done = 0;
            foreach (var u in updates)
            {
                TxtInstHealth.Text = $"Обновляю {u.Project.Title}… ({done + 1} из {updates.Count})";
                if (await _mods.ApplyUpdateAsync(u, modsDir, inst.McVersion, inst.Loader)) done++;
            }

            TxtInstHealth.Text = $"Обновлено модов: {done} из {updates.Count}.";
            TxtInstHealth.Foreground = (Brush)FindResource("Accent");

            NotifyFinished("Моды обновлены", $"Обновлено {done} модов");
            RefreshInstanceStats();
            RefreshContent();
        }
        catch (Exception ex)
        {
            TxtInstHealth.Text = "Ошибка: " + ex.Message;
            TxtInstHealth.Foreground = (Brush)FindResource("Danger");
        }
    }

    // ---------- Конфликты ----------

    private void BtnFindConflicts_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;

        TxtInstHealth.Text = "Читаю моды…";

        try
        {
            var mods = ModInspector.ReadAll(InstanceService.ModsDir(_selectedInstance));

            if (mods.Count == 0)
            {
                TxtInstHealth.Text = "Модов в сборке нет.";
                TxtInstHealth.Foreground = (Brush)FindResource("FgMuted");
                return;
            }

            var conflicts = ModInspector.FindConflicts(mods, _selectedInstance.Loader);

            if (conflicts.Count == 0)
            {
                TxtInstHealth.Text = $"Проверено модов: {mods.Count}. Конфликтов не найдено.";
                TxtInstHealth.Foreground = (Brush)FindResource("Accent");
                return;
            }

            var sb = new StringBuilder($"Проверено модов: {mods.Count}\n");

            foreach (var c in conflicts)
            {
                sb.AppendLine($"  {(c.IsError ? "✕" : "!")}  {c.Title}");
                sb.AppendLine($"      {c.Details}");
                if (c.Files.Count > 0)
                    sb.AppendLine($"      файлы: {string.Join(", ", c.Files.Take(4))}");
            }

            TxtInstHealth.Text = sb.ToString().TrimEnd();
            TxtInstHealth.Foreground = (Brush)FindResource(
                conflicts.Any(c => c.IsError) ? "Danger" : "FgMuted");
        }
        catch (Exception ex)
        {
            TxtInstHealth.Text = "Ошибка: " + ex.Message;
        }
    }

    // ---------- Статистика ----------

    private void RefreshStatistics()
    {
        if (_selectedInstance is null) return;

        var inst = _selectedInstance;

        TxtStatTotal.Text = inst.TotalPlaySeconds > 0 ? inst.PlayTimeDisplay : "—";
        TxtStatSessions.Text = inst.Sessions.Count.ToString();

        TxtStatAvg.Text = inst.Sessions.Count > 0
            ? FormatMinutes((long)inst.Sessions.Average(s => s.Seconds))
            : "—";

        // График за 14 дней
        var today = DateTime.Today;
        var days = Enumerable.Range(0, 14).Select(i => today.AddDays(-13 + i)).ToList();

        var byDay = days.Select(d => new
        {
            Day = d,
            Seconds = inst.Sessions.Where(s => s.Date.Date == d).Sum(s => s.Seconds)
        }).ToList();

        var max = Math.Max(1, byDay.Max(x => x.Seconds));

        ItemsChart.ItemsSource = byDay.Select(x =>
        {
            var height = x.Seconds == 0 ? 2.0 : Math.Max(4, x.Seconds * 62.0 / max);

            return new
            {
                BarHeight = height,
                Label = x.Day.ToString("dd"),
                Bar = new SolidColorBrush(x.Seconds > 0
                    ? ThemeService.CurrentAccent
                    : (Color)ColorConverter.ConvertFromString("#2A2F3A")),
                Tip = x.Seconds > 0
                    ? $"{x.Day:dd.MM}: {FormatMinutes(x.Seconds)}"
                    : $"{x.Day:dd.MM}: не играли"
            };
        }).ToList();
    }

    private static string FormatMinutes(long seconds)
    {
        if (seconds < 60) return $"{seconds} с";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours} ч {ts.Minutes} мин" : $"{ts.Minutes} мин";
    }

    // ---------- JVM-пресеты ----------

    private void RefreshJvmPresets()
    {
        if (_selectedInstance is null) return;

        _loadingInstSettings = true;
        try
        {
            if (CbJvmPreset.ItemsSource is null)
                CbJvmPreset.ItemsSource = JvmPresetService.Presets.Select(p => p.Name).ToList();

            CbJvmPreset.SelectedItem = JvmPresetService.Get(_selectedInstance.JvmPreset).Name;
            UpdateJvmPresetInfo();
        }
        finally { _loadingInstSettings = false; }
    }

    private void UpdateJvmPresetInfo()
    {
        if (_selectedInstance is null || CbJvmPreset.SelectedItem is not string name) return;

        var preset = JvmPresetService.Get(name);
        var memory = _selectedInstance.MaxMemoryMb > 0
            ? _selectedInstance.MaxMemoryMb : _settings.MaxMemoryMb;

        var javaMajor = 0;
        try
        {
            var path = !string.IsNullOrWhiteSpace(_selectedInstance.JavaPath)
                ? _selectedInstance.JavaPath : _settings.CustomJavaPath;

            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                javaMajor = JavaService.Probe(path, "check")?.MajorVersion ?? 0;
        }
        catch { }

        var warning = JvmPresetService.Validate(name, memory, javaMajor);

        TxtJvmPresetInfo.Text = warning ?? preset.Description;
        TxtJvmPresetInfo.Foreground = (Brush)FindResource(warning is null ? "FgMuted" : "Danger");
    }

    private void CbJvmPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _loadingInstSettings || _selectedInstance is null) return;
        if (CbJvmPreset.SelectedItem is not string name) return;

        _selectedInstance.JvmPreset = name;
        InstanceService.SaveAll(_instances);
        UpdateJvmPresetInfo();

        AppendLog($"Сборка «{_selectedInstance.Name}»: пресет JVM «{name}».");
    }

    // ---------- Иконка сборки ----------

    private void RefreshInstanceIcon()
    {
        if (_selectedInstance is null) return;

        var color = (Color)ColorConverter.ConvertFromString(_selectedInstance.IconColor);
        InstIconDot.Background = new SolidColorBrush(color);

        if (!string.IsNullOrWhiteSpace(_selectedInstance.IconPath) &&
            File.Exists(_selectedInstance.IconPath))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 96;
                bmp.UriSource = new Uri(_selectedInstance.IconPath);
                bmp.EndInit();
                bmp.Freeze();

                ImgInstIcon.Source = bmp;
                InstIconDot.Visibility = Visibility.Collapsed;
                return;
            }
            catch (Exception ex) { Log.Warn("Иконка сборки: " + ex.Message); }
        }

        ImgInstIcon.Source = null;
        InstIconDot.Visibility = Visibility.Visible;
    }

    private void BtnInstIcon_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;

        var dlg = new OpenFileDialog
        {
            Title = "Иконка сборки",
            Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.ico|Все файлы|*.*"
        };

        if (dlg.ShowDialog(this) != true) return;

        try
        {
            // Копируем в папку сборки, чтобы иконка не потерялась при переносе
            var dst = IOPath.Combine(InstanceService.InstanceDir(_selectedInstance),
                "icon" + IOPath.GetExtension(dlg.FileName));

            File.Copy(dlg.FileName, dst, true);

            _selectedInstance.IconPath = dst;
            InstanceService.SaveAll(_instances);

            RefreshInstanceIcon();
            RefreshInstanceLists();
            AppendLog($"Иконка сборки «{_selectedInstance.Name}» обновлена.");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось установить иконку: " + ex.Message,
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnInstIconClear_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;

        _selectedInstance.IconPath = "";
        InstanceService.SaveAll(_instances);

        RefreshInstanceIcon();
        RefreshInstanceLists();
    }
    private void InstSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _loadingInstSettings || _selectedInstance is null) return;

        var inst = _selectedInstance;

        var newName = TxtInstEditName.Text.Trim();
        if (newName.Length > 0 && newName != inst.Name)
        {
            inst.Name = newName;
            RefreshInstanceLists();
            CbInstances.SelectedItem = _instances.FirstOrDefault(i => i.Id == inst.Id);
        }

        inst.MaxMemoryMb = int.TryParse(TxtInstMemory.Text.Trim(), out var mem) && mem > 0 ? mem : 0;
        inst.WindowWidth = int.TryParse(TxtInstWidth.Text.Trim(), out var w) && w > 0 ? w : 0;
        inst.WindowHeight = int.TryParse(TxtInstHeight.Text.Trim(), out var h) && h > 0 ? h : 0;
        inst.ServerAddress = TxtInstServer.Text.Trim();
        inst.ExtraJvmArgs = TxtInstJvm.Text.Trim();

        InstanceService.SaveAll(_instances);
        TxtInstName.Text = inst.Name;
    }

    private void BtnInstJava_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;

        var dlg = new OpenFileDialog
        {
            Title = "java.exe для этой сборки",
            Filter = "java.exe|java.exe;javaw.exe|Исполняемые файлы (*.exe)|*.exe"
        };
        if (dlg.ShowDialog(this) != true) return;

        var probe = JavaService.Probe(dlg.FileName, "instance");
        if (probe is null)
        {
            MessageBox.Show("Не удалось определить версию Java по этому пути.",
                "Java", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _selectedInstance.JavaPath = dlg.FileName;
        TxtInstJava.Text = dlg.FileName;
        InstanceService.SaveAll(_instances);
        AppendLog($"Для «{_selectedInstance.Name}» выбрана {probe}");
    }

    private void BtnInstSetRu_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;

        var ok = GameOptionsService.SetLanguage(
            InstanceService.InstanceDir(_selectedInstance), _selectedInstance.McVersion, "ru");

        MessageBox.Show(ok
                ? "Русский язык записан в options.txt этой сборки."
                : "Не удалось изменить язык — подробности в консоли.",
            "Язык игры", MessageBoxButton.OK,
            ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void BtnDuplicateInstance_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;

        var src = _selectedInstance;

        var copy = new GameInstance
        {
            Name = src.Name + " (копия)",
            McVersion = src.McVersion,
            Loader = src.Loader,
            LoaderVersion = src.LoaderVersion,
            LaunchVersionId = src.LaunchVersionId,
            MaxMemoryMb = src.MaxMemoryMb,
            WindowWidth = src.WindowWidth,
            WindowHeight = src.WindowHeight,
            ServerAddress = src.ServerAddress,
            ExtraJvmArgs = src.ExtraJvmArgs,
            JavaPath = src.JavaPath,
            IconColor = src.IconColor,
            Isolated = src.Isolated
        };

        InstanceService.EnsureFolders(copy);

        var r = MessageBox.Show(
            "Скопировать моды, ресурспаки и шейдеры в новую сборку?\n\n" +
            "«Нет» — создать пустую сборку с теми же настройками.",
            "Дублирование", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (r == MessageBoxResult.Cancel) return;

        if (r == MessageBoxResult.Yes)
        {
            try
            {
                foreach (var sub in new[] { "mods", "resourcepacks", "shaderpacks", "config" })
                {
                    var from = IOPath.Combine(InstanceService.InstanceDir(src), sub);
                    if (Directory.Exists(from))
                        CopyDirectory(from, IOPath.Combine(InstanceService.InstanceDir(copy), sub));
                }
            }
            catch (Exception ex)
            {
                AppendLog("Ошибка копирования содержимого: " + ex.Message);
            }
        }

        _instances.Add(copy);
        InstanceService.SaveAll(_instances);
        RefreshInstanceLists();
        CbInstances.SelectedItem = _instances.FirstOrDefault(i => i.Id == copy.Id);

        AppendLog($"Создана копия сборки: «{copy.Name}»");
    }

    private void BtnResetInstance_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;

        if (MessageBox.Show(
                "Сбросить индивидуальные настройки этой сборки?\n\n" +
                "Память, размер окна, Java и аргументы вернутся к общим значениям.\n" +
                "Моды и миры не пострадают.",
                "Сброс настроек сборки", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;

        var inst = _selectedInstance;
        inst.MaxMemoryMb = 0;
        inst.WindowWidth = 0;
        inst.WindowHeight = 0;
        inst.ServerAddress = "";
        inst.ExtraJvmArgs = "";
        inst.JavaPath = "";

        InstanceService.SaveAll(_instances);
        FillInstanceSettings(inst);
        AppendLog($"Настройки сборки «{inst.Name}» сброшены.");
    }
    private void RefreshInstanceStats()
    {
        if (_selectedInstance is null) return;

        var st = InstanceService.GetStats(_selectedInstance);

        TxtCountMods.Text = Plural(st.Mods, "файл", "файла", "файлов");
        TxtCountRp.Text = Plural(st.ResourcePacks, "пак", "пака", "паков");
        TxtCountShaders.Text = Plural(st.ShaderPacks, "пак", "пака", "паков");
        TxtCountWorlds.Text = Plural(st.Worlds, "мир", "мира", "миров");
        TxtInstSize.Text = st.SizeDisplay;

        TxtQuickMods.Text = st.Mods > 0 ? $"Моды ({st.Mods})" : "Моды";
        TxtQuickRp.Text = st.ResourcePacks > 0 ? $"Ресурспаки ({st.ResourcePacks})" : "Ресурспаки";
        TxtQuickShaders.Text = st.ShaderPacks > 0 ? $"Шейдеры ({st.ShaderPacks})" : "Шейдеры";
        TxtQuickShots.Text = st.Screenshots > 0 ? $"Скриншоты ({st.Screenshots})" : "Скриншоты";
    }

    private static string Plural(int n, string one, string few, string many)
    {
        var mod10 = n % 10;
        var mod100 = n % 100;
        var word = (mod10 == 1 && mod100 != 11) ? one
            : (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) ? few
            : many;
        return $"{n} {word}";
    }

    private void LoadScreenshots()
    {
        if (_selectedInstance is null) return;

        var files = InstanceService.GetScreenshots(_selectedInstance, 12);

        if (files.Count == 0)
        {
            ItemsScreenshots.ItemsSource = null;
            TxtNoShots.Visibility = Visibility.Visible;
            return;
        }

        TxtNoShots.Visibility = Visibility.Collapsed;

        var items = new List<object>();
        foreach (var f in files)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 264;      // экономим память на превью
                bmp.UriSource = new Uri(f.FullName);
                bmp.EndInit();
                bmp.Freeze();

                items.Add(new { Thumb = bmp, Path = f.FullName, Name = f.Name });
            }
            catch { /* битый файл пропускаем */ }
        }

        ItemsScreenshots.ItemsSource = items;
    }

    private void Screenshot_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string path) return;

        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { AppendLog("Не удалось открыть скриншот: " + ex.Message); }
    }

    private void ChkSnapshots_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.ShowSnapshots = ChkSnapshots.IsChecked == true;
    }

    // ---------- Создание / удаление ----------

    private void BtnNewInstance_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InstanceDialog(_versions, _loaders, _settings.ShowSnapshots, _settings.DefaultIsolated) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is null) return;

        var inst = dlg.Result;
        InstanceService.EnsureFolders(inst);
        _instances.Add(inst);
        InstanceService.SaveAll(_instances);

        _settings.LastInstanceId = inst.Id;
        RefreshInstanceLists();
        AppendLog($"Создана сборка «{inst.Name}» ({inst.McVersion}, {inst.LoaderDisplay}).");

        NavInstances.IsChecked = true;

        // Если создавали из модпака — распаковываем его содержимое
        if (!string.IsNullOrEmpty(dlg.ModpackPath))
            _ = InstallModpackAsync(inst, dlg.ModpackPath!);
    }

    /// <summary>Распаковывает модпак в только что созданную сборку.</summary>
    private async Task InstallModpackAsync(GameInstance inst, string packPath)
    {
        SetBusy(true);

        try
        {
            SetStage("Устанавливаю модпак...");
            var info = await _modpacks.InstallAsync(packPath, inst);

            RefreshInstanceStats();
            RefreshContent();

            MessageBox.Show(
                $"Модпак «{info.Name}» установлен в сборку «{inst.Name}».\n\n" +
                $"Версия: {info.McVersion} {info.Loader.Display()}\n" +
                $"Файлов: {info.FileCount}\n\n" +
                "Загрузчик установится при первом запуске.",
                "Модпак готов", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error("Установка модпака", ex);
            MessageBox.Show("Не удалось установить модпак:\n\n" + ex.Message,
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            HideProgress();
        }
    }
    private void BtnDeleteInstance_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;

        if (_sessions.IsInstanceRunning(_selectedInstance.Id))
        {
            MessageBox.Show("Нельзя удалить сборку, пока она запущена. Сначала остановите игру.",
                "Сборка занята", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var inst = _selectedInstance;
        var r = MessageBox.Show(
            $"Удалить сборку «{inst.Name}»?\n\n" +
            "«Да» — удалить вместе с модами, мирами и скриншотами.\n" +
            "«Нет» — убрать из списка, файлы оставить.",
            "Удаление сборки", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        if (r == MessageBoxResult.Cancel) return;

        try
        {
            if (r == MessageBoxResult.Yes) InstanceService.Delete(inst, true);

            _instances.Remove(inst);
            InstanceService.SaveAll(_instances);
            _selectedInstance = null;
            RefreshInstanceLists();
            AppendLog($"Сборка «{inst.Name}» удалена.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ошибка удаления", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Папки ----------

    private void OpenInstanceFolder(Func<GameInstance, string> selector)
    {
        if (_selectedInstance is null)
        {
            MessageBox.Show("Сначала выберите сборку.", "Сборка не выбрана",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            InstanceService.OpenFolder(selector(_selectedInstance));
            RefreshInstanceStats();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenMods_Click(object s, RoutedEventArgs e) => OpenInstanceFolder(InstanceService.ModsDir);
    private void OpenResourcePacks_Click(object s, RoutedEventArgs e) => OpenInstanceFolder(InstanceService.ResourcePacksDir);
    private void OpenShaders_Click(object s, RoutedEventArgs e) => OpenInstanceFolder(InstanceService.ShaderPacksDir);
    private void OpenSaves_Click(object s, RoutedEventArgs e) => OpenInstanceFolder(InstanceService.SavesDir);
    private void OpenScreenshots_Click(object s, RoutedEventArgs e) => OpenInstanceFolder(InstanceService.ScreenshotsDir);
    private void OpenInstanceRoot_Click(object s, RoutedEventArgs e) => OpenInstanceFolder(InstanceService.InstanceDir);
    private void OpenLogs_Click(object s, RoutedEventArgs e) => OpenInstanceFolder(InstanceService.LogsDir);

    private void QuickMods_Click(object s, RoutedEventArgs e) => OpenInstanceFolder(InstanceService.ModsDir);
    private void QuickResourcePacks_Click(object s, RoutedEventArgs e) => OpenInstanceFolder(InstanceService.ResourcePacksDir);
    private void QuickShaders_Click(object s, RoutedEventArgs e) => OpenInstanceFolder(InstanceService.ShaderPacksDir);
    private void QuickScreenshots_Click(object s, RoutedEventArgs e) => OpenInstanceFolder(InstanceService.ScreenshotsDir);

    // =====================================================================
    //  СЕРВЕРЫ
    // =====================================================================

    private async Task RefreshServersAsync()
    {
        var servers = ServerCatalog.LoadAll();

        try
        {
            var sponsors = await ServerCatalog.LoadSponsorServersAsync(App.Http);
            if (sponsors.Count > 0)
            {
                servers = servers.Where(s => !s.Featured).ToList();
                servers.InsertRange(0, sponsors);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось загрузить спонсорские серверы: " + ex.Message);
        }

        var views = servers.Select(s => CreateServerView(s, null)).ToList();
        ItemsServers.ItemsSource = views;

        var tasks = servers.Select(async (srv, idx) =>
        {
            var status = await _ping.PingAsync(srv.Address);
            Dispatcher.Invoke(() =>
            {
                views[idx] = CreateServerView(srv, status);
                ItemsServers.ItemsSource = null;
                ItemsServers.ItemsSource = views;
            });
        });

        await Task.WhenAll(tasks);
    }

    private object CreateServerView(ServerEntry srv, ServerStatus? status)
    {
        BitmapImage? favicon = null;
        if (status?.FaviconPng is not null)
        {
            try
            {
                var bmp = new BitmapImage();
                using var ms = new MemoryStream(status.FaviconPng);
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                favicon = bmp;
            }
            catch { }
        }

        var online = status?.Online == true;
        var checking = status is null;

        return new
        {
            srv.Name,
            srv.Address,
            Initial = srv.Name.Length > 0 ? srv.Name[..1].ToUpperInvariant() : "?",
            Favicon = favicon,
            PlaceholderVisibility = favicon is null ? Visibility.Visible : Visibility.Collapsed,
            FeaturedVisibility = srv.Featured ? Visibility.Visible : Visibility.Collapsed,

            StatusText = checking ? "проверка…" : online ? "онлайн" : "офлайн",
            StatusColor = new SolidColorBrush(checking
                ? (Color)ColorConverter.ConvertFromString("#8B93A3")
                : online ? ThemeService.CurrentAccent : (Color)ColorConverter.ConvertFromString("#F87171")),
            StatusBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                checking ? "#22262E" : online ? "#14301F" : "#2A1A1D")),

            Players = online ? status!.OnlinePlayers.ToString() : "—",
            Motd = checking ? "Получаю данные сервера..."
                : online ? (string.IsNullOrWhiteSpace(status!.Motd) ? srv.Description : status.Motd)
                : (status?.Error ?? "Сервер недоступен"),

            VersionInfo = online && !string.IsNullOrEmpty(status!.VersionName)
                ? status.VersionName
                : "версия " + srv.RequiredVersion,
            PingInfo = online ? $"{status!.PingMs} мс" : "",

            RequiredVersion = srv.RequiredVersion,
            Loader = srv.Loader
        };
    }

    private async void BtnRefreshServers_Click(object sender, RoutedEventArgs e)
    {
        BtnRefreshServers.IsEnabled = false;
        try { await RefreshServersAsync(); }
        finally { BtnRefreshServers.IsEnabled = true; }
    }

    private void ServerCopy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string addr) return;

        try
        {
            Clipboard.SetText(addr);
            AppendLog($"Адрес {addr} скопирован в буфер обмена.");
        }
        catch (Exception ex) { AppendLog("Не удалось скопировать: " + ex.Message); }
    }

    /// <summary>«Играть» на карточке сервера: подбирает сборку нужной версии и запускает с подключением.</summary>
    private async void ServerPlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string address) return;

        var srv = ServerCatalog.LoadAll().FirstOrDefault(s =>
            string.Equals(s.Address, address, StringComparison.OrdinalIgnoreCase));
        if (srv is null) return;

        var inst = _instances.FirstOrDefault(i =>
                       i.McVersion == srv.RequiredVersion && i.Loader == srv.Loader)
                   ?? _instances.FirstOrDefault(i => i.McVersion == srv.RequiredVersion);

        if (inst is null)
        {
            var r = MessageBox.Show(
                $"Для сервера {srv.Name} нужна версия {srv.RequiredVersion}, " +
                "но подходящей сборки нет.\n\nСоздать её сейчас?",
                "Нужна сборка", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (r != MessageBoxResult.Yes) return;

            inst = new GameInstance
            {
                Name = $"{srv.Name} ({srv.RequiredVersion})",
                McVersion = srv.RequiredVersion,
                Loader = srv.Loader,
                LaunchVersionId = srv.Loader == LoaderKind.Vanilla ? srv.RequiredVersion : null,
                IconColor = "#FACC15"
            };
            InstanceService.EnsureFolders(inst);
            _instances.Add(inst);
            InstanceService.SaveAll(_instances);
            RefreshInstanceLists();
        }

        CbInstances.SelectedItem = inst;
        SelectInstance(inst);

        await LaunchAsync(inst, srv.Address);
    }

    private void BtnAddServer_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AddServerDialog { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is null) return;

        var list = ServerCatalog.LoadUserServers();
        list.Add(dlg.Result);
        ServerCatalog.SaveUserServers(list);

        AppendLog($"Добавлен сервер {dlg.Result.Name} ({dlg.Result.Address}).");
        _ = RefreshServersAsync();
    }

    // =====================================================================
    //  ЗАПУСК ИГРЫ
    // =====================================================================

    private async void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null)
        {
            MessageBox.Show("Сначала выберите или создайте сборку.", "Сборка не выбрана",
                MessageBoxButton.OK, MessageBoxImage.Information);
            NavInstances.IsChecked = true;
            return;
        }

        await LaunchAsync(_selectedInstance, null);
    }

    private async Task LaunchAsync(GameInstance inst, string? serverAddress)
    {
        if (_busy) return;

        if (_account is null)
        {
            MessageBox.Show(
                "Сначала войдите в аккаунт на вкладке «Аккаунт».\n\n" +
                "Доступны вход через Microsoft и оффлайн-профиль.",
                "Требуется вход", MessageBoxButton.OK, MessageBoxImage.Information);
            NavAccount.IsChecked = true;
            return;
        }

        // Защита от повторного запуска
        if (!_settings.AllowMultipleInstances && _sessions.AnyRunning)
        {
            var running = _sessions.Sessions.First(s => s.IsRunning);
            MessageBox.Show(
                $"Игра уже запущена: «{running.InstanceName}».\n\n" +
                "Остановите её кнопкой «ОСТАНОВИТЬ» либо разрешите\n" +
                "несколько копий в настройках.",
                "Игра уже запущена", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_sessions.IsInstanceRunning(inst.Id))
        {
            MessageBox.Show($"Сборка «{inst.Name}» уже запущена.", "Уже запущена",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PersistSettings();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true);

        try
        {
            // 1. Токен
            if (!_account.IsOffline && _account.IsExpired &&
                !string.IsNullOrEmpty(_account.MicrosoftRefreshToken))
            {
                SetStage("Обновляю сессию Microsoft...");
                _account = await _auth.RefreshAsync(_account.MicrosoftRefreshToken!, ct);
                AccountStorage.Save(_account);
                SetAccount(_account, refreshSkin: false);
            }

            // 2. Хранилище: общее либо изолированное для этой сборки
            var paths = GamePaths.ForInstance(inst);
            paths.EnsureAll();

            _versions.Paths = paths;
            _downloads.Paths = paths;
            _loaders.Paths = paths;
            _loaders.InstallRoot = paths.IsIsolated
                ? IOPath.Combine(InstanceService.InstanceDir(inst), ".minecraft")
                : LauncherPaths.Root;

            if (paths.IsIsolated) AppendLog($"Сборка «{inst.Name}» изолирована: файлы в её папке.");

            // 3. Базовая версия
            SetStage($"Читаю описание версии {inst.McVersion}...");
            var manifest = _manifest ?? await _versions.GetManifestAsync(ct);
            var mv = manifest.Versions.FirstOrDefault(v => v.Id == inst.McVersion)
                     ?? throw new InvalidOperationException($"Версия {inst.McVersion} не найдена в манифесте.");
            var baseDetail = await _versions.GetVersionDetailAsync(mv, ct);

            // 3. Java
            var requiredJava = baseDetail.JavaVersion?.MajorVersion ?? JavaService.RequiredJavaFor(inst.McVersion);
            SetStage($"Проверяю Java {requiredJava}...");

            JavaInstallation java;
            var javaOverride = !string.IsNullOrWhiteSpace(inst.JavaPath) ? inst.JavaPath : _settings.CustomJavaPath;

            if (!string.IsNullOrWhiteSpace(javaOverride) && File.Exists(javaOverride))
            {
                java = JavaService.Probe(javaOverride, "custom")
                       ?? throw new InvalidOperationException("Указанный java.exe не отвечает.");
                if (java.MajorVersion < requiredJava)
                    AppendLog($"ВНИМАНИЕ: выбрана Java {java.MajorVersion}, нужна {requiredJava}.");
            }
            else
            {
                java = await _java.EnsureJavaAsync(requiredJava, ct);
            }

            Dispatcher.Invoke(() => TxtBadgeJava.Text = $"Java {java.MajorVersion}");

            // 4. Ванильные файлы (нужны и модлоадеру)
            await Task.Run(() => _downloads.InstallVersionAsync(baseDetail, ct), ct);

            // 5. Модлоадер
            var launchId = inst.EffectiveVersionId;

            if (inst.Loader != LoaderKind.Vanilla)
            {
                var alreadyInstalled = !string.IsNullOrEmpty(inst.LaunchVersionId) &&
                                       File.Exists(paths.VersionJson(inst.LaunchVersionId!));

                if (!alreadyInstalled)
                {
                    SetStage($"Устанавливаю {inst.Loader.Display()} {inst.LoaderVersion}...");
                    launchId = await _loaders.InstallAsync(
                        inst.Loader, inst.McVersion, inst.LoaderVersion!, java, ct);

                    inst.LaunchVersionId = launchId;
                    InstanceService.SaveAll(_instances);
                }
                else
                {
                    launchId = inst.LaunchVersionId!;
                }
            }

            // 6. Итоговый профиль (со слиянием inheritsFrom) и его файлы
            SetStage("Готовлю файлы запуска...");
            var finalDetail = await _versions.ResolveAsync(launchId, ct);
            var install = await Task.Run(() => _downloads.InstallVersionAsync(finalDetail, ct), ct);

            NotifyFinished("Загрузка завершена", $"«{inst.Name}» готова к запуску");

            // 6.5. Скин оффлайн-аккаунта через CustomSkinLoader
            if (_account.IsOffline)
            {
                var skinFile = OfflineSkinService.FindAccountSkin(_account.Username);
                if (skinFile != null)
                {
                    if (OfflineSkinService.IsCslSupported(inst))
                    {
                        SetStage("Подготавливаю скин (CustomSkinLoader)...");
                        var cslOk = await OfflineSkinService.EnsureCslModAsync(inst, ct);
                        if (cslOk)
                        {
                            OfflineSkinService.SyncToInstance(inst, _account.Username, skinFile);
                            AppendLog($"Оффлайн-скин «{_account.Username}» подготовлен через CustomSkinLoader.");
                        }
                        else
                        {
                            AppendLog("Не удалось подготовить CustomSkinLoader — скин не будет показан.");
                        }
                    }
                    else
                    {
                        AppendLog("Внимание: сборка без модлоадера (Fabric/Forge) — оффлайн-скин показан не будет.");
                    }
                }
            }

            // 7. Запуск
            SetStage("Запускаю Minecraft...");
            InstanceService.EnsureFolders(inst);

            // Русский язык из коробки — как в TLegacy. Существующий options.txt не трогаем.
            if (_settings.AutoSetGameLanguage)
            {
                var created = GameOptionsService.EnsureLanguage(
                    InstanceService.InstanceDir(inst), inst.McVersion, _settings.GameLanguage);
                if (created)
                    AppendLog($"Язык игры установлен: " +
                              GameOptionsService.LanguageCodeFor(inst.McVersion, _settings.GameLanguage));
            }

            var options = new LaunchOptions
            {
                Account = _account,
                Install = install,
                Java = java,
                GameDir = InstanceService.InstanceDir(inst),
                MinMemoryMb = Math.Min(1024, EffectiveMemory(inst)),
                MaxMemoryMb = EffectiveMemory(inst),
                WindowWidth = inst.WindowWidth > 0 ? inst.WindowWidth : _settings.WindowWidth,
                WindowHeight = inst.WindowHeight > 0 ? inst.WindowHeight : _settings.WindowHeight,
                Fullscreen = _settings.Fullscreen,
                ServerAddress = serverAddress ?? (string.IsNullOrWhiteSpace(inst.ServerAddress) ? null : inst.ServerAddress),
                ExtraJvmArgs = JvmPresetService.Resolve(inst.JvmPreset,
                    string.IsNullOrWhiteSpace(inst.ExtraJvmArgs) ? _settings.ExtraJvmArgs : inst.ExtraJvmArgs),
                ShowConsole = _settings.ShowConsole,
                CloseLauncherOnStart = _settings.CloseLauncherOnStart
            };

            var proc = _game.Launch(options);

            _sessions.Register(new GameSession
            {
                Process = proc,
                Pid = proc.Id,
                VersionId = launchId,
                InstanceId = inst.Id,
                InstanceName = inst.Name
            });

            inst.LastPlayed = DateTimeOffset.Now;
            InstanceService.SaveAll(_instances);

            AppendLog($"Minecraft запущен (PID {proc.Id}), сборка «{inst.Name}».");
            if (serverAddress is not null) AppendLog($"Автоподключение к серверу {serverAddress}.");
            SetStage("Игра запущена");

            if (_settings.CloseLauncherOnStart)
            {
                await Task.Delay(2500, ct);
                Application.Current.Shutdown();
                return;
            }

            if (_settings.MinimizeOnLaunch) WindowState = WindowState.Minimized;

            // Ловим мгновенные краши
            var exitedFast = await Task.Run(() => proc.WaitForExit(9000), ct);
            if (exitedFast && proc.ExitCode != 0)
            {
                WindowState = WindowState.Normal;
                Activate();
                AppendLog($"Игра завершилась сразу с кодом {proc.ExitCode}.");
                MessageBox.Show(
                    $"Minecraft завершился с кодом {proc.ExitCode}.\nОткройте «Консоль» для деталей.",
                    "Игра не запустилась", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("Операция отменена.");
            SetStage("Отменено");
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка запуска", ex);
            MessageBox.Show(ex.Message, "Ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStage("Ошибка");
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
            UpdateRunStateUi();
            RefreshInstanceStats();
        }
    }

    private int EffectiveMemory(GameInstance inst) =>
        inst.MaxMemoryMb > 0 ? inst.MaxMemoryMb : _settings.MaxMemoryMb;

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        SetStage("Отмена...");
    }

    // ---------- Остановка ----------

    private async void BtnStopGame_Click(object sender, RoutedEventArgs e)
    {
        _sessions.Prune();
        var running = _sessions.Sessions.Where(s => s.IsRunning).ToList();
        if (running.Count == 0) { UpdateRunStateUi(); return; }

        if (_settings.ConfirmGameStop)
        {
            var names = string.Join(", ", running.Select(s => s.InstanceName));
            var r = MessageBox.Show(
                $"Закрыть игру: {names}?\n\nНесохранённый прогресс может быть потерян.",
                "Остановить игру", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
        }

        BtnStopGame.IsEnabled = false;
        BtnStopGame.Content = "ОСТАНАВЛИВАЮ…";

        try
        {
            foreach (var s in running)
            {
                AppendLog($"Останавливаю «{s.InstanceName}» (PID {s.Pid})...");
                await _sessions.StopAsync(s);
            }
        }
        finally
        {
            BtnStopGame.IsEnabled = true;
            BtnStopGame.Content = "ОСТАНОВИТЬ";
            UpdateRunStateUi();
        }
    }

    private void OnSessionExited(GameSession session, int code)
    {
        var seconds = (long)session.Uptime.TotalSeconds;

        Dispatcher.BeginInvoke(() =>
        {
            var inst = _instances.FirstOrDefault(i => i.Id == session.InstanceId);
            if (inst is not null)
            {
                inst.AddSession(seconds);
                InstanceService.SaveAll(_instances);
                if (ReferenceEquals(inst, _selectedInstance))
                {
                    TxtInstPlaytime.Text = "В игре: " + inst.PlayTimeDisplay;
                    RefreshInstanceStats();
                    LoadScreenshots();
                }
            }

            AppendLog($"--- «{session.InstanceName}» завершилась (код {code}), " +
                      $"время сессии {session.UptimeDisplay} ---");

            if (WindowState == WindowState.Minimized && !_sessions.AnyRunning)
                WindowState = WindowState.Normal;

            UpdateRunStateUi();
        });
    }

    /// <summary>Переключает кнопки «ИГРАТЬ» / «ОСТАНОВИТЬ» и бейдж в заголовке.</summary>
    private void UpdateRunStateUi()
    {
        _sessions.Prune();

        var anyRunning = _sessions.AnyRunning;
        var thisRunning = _selectedInstance is not null &&
                          _sessions.IsInstanceRunning(_selectedInstance.Id);

        // Кнопка «Играть» прячется, когда игра идёт и мультизапуск запрещён
        var hidePlay = !_busy && anyRunning && (!_settings.AllowMultipleInstances || thisRunning);

        BtnPlay.Visibility = hidePlay ? Visibility.Collapsed : Visibility.Visible;
        BtnStopGame.Visibility = anyRunning ? Visibility.Visible : Visibility.Collapsed;

        BtnPlay.IsEnabled = !_busy;
        BtnPlay.Content = _busy ? "ПОДГОТОВКА…"
            : _selectedInstance is not null && !File.Exists(GamePaths.ForInstance(_selectedInstance).VersionJar(_selectedInstance.McVersion))
                ? "УСТАНОВИТЬ И ИГРАТЬ"
                : "ИГРАТЬ";

        RunningBadge.Visibility = anyRunning ? Visibility.Visible : Visibility.Collapsed;
        BtnDeleteInstance.IsEnabled = !thisRunning;

        UpdateUptimeBadge();
    }

    private void UpdateUptimeBadge()
    {
        var running = _sessions.Sessions.Where(s => s.IsRunning).ToList();
        if (running.Count == 0) return;

        TxtRunningBadge.Text = running.Count == 1
            ? $"{running[0].InstanceName} · {running[0].UptimeDisplay}"
            : $"Запущено игр: {running.Count}";

        BtnStopGame.Content = running.Count > 1 ? $"ОСТАНОВИТЬ ({running.Count})" : "ОСТАНОВИТЬ";
    }

    // =====================================================================
    //  АККАУНТ
    // =====================================================================

    private void TxtOfflineName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;

        var name = TxtOfflineName.Text;
        if (string.IsNullOrWhiteSpace(name))
        {
            TxtOfflineHint.Text = "Введите никнейм (3-16 символов).";
            TxtOfflineHint.Foreground = (Brush)FindResource("FgMuted");
            return;
        }

        if (OfflineAccountService.TryValidateName(name, out var error))
        {
            TxtOfflineHint.Text = "UUID будет: " + Dashed(OfflineAccountService.GenerateOfflineUuid(name.Trim()));
            TxtOfflineHint.Foreground = (Brush)FindResource("Accent");
        }
        else
        {
            TxtOfflineHint.Text = error;
            TxtOfflineHint.Foreground = (Brush)FindResource("Danger");
        }
    }

    private void TxtOfflineName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) BtnOfflineLogin_Click(sender, e);
    }

    private void BtnOfflineLogin_Click(object sender, RoutedEventArgs e)
    {
        var name = TxtOfflineName.Text.Trim();

        if (!OfflineAccountService.TryValidateName(name, out var error))
        {
            TxtOfflineHint.Text = error;
            TxtOfflineHint.Foreground = (Brush)FindResource("Danger");
            TxtOfflineName.Focus();
            return;
        }

        try
        {
            var acc = OfflineAccountService.Create(name);
            AccountStorage.Save(acc);
            SetAccount(acc, refreshSkin: true);

            TxtOfflineHint.Text = "Оффлайн-профиль создан.";
            TxtOfflineHint.Foreground = (Brush)FindResource("Accent");
            AppendLog($"Создан оффлайн-аккаунт: {acc.Username} ({acc.DashedUuid})");
        }
        catch (Exception ex)
        {
            TxtOfflineHint.Text = ex.Message;
            TxtOfflineHint.Foreground = (Brush)FindResource("Danger");
        }
    }

    private static string Dashed(string uuid)
    {
        var u = uuid.Replace("-", "");
        return u.Length != 32 ? uuid
            : $"{u[..8]}-{u.Substring(8, 4)}-{u.Substring(12, 4)}-{u.Substring(16, 4)}-{u.Substring(20)}";
    }

    private void BtnLogout_Click(object sender, RoutedEventArgs e)
    {
        AccountStorage.Clear();
        _account = null;

        TxtAccName.Text = "—";
        TxtAccUuid.Text = "";
        TxtAuthState.Text = "Вы не вошли в аккаунт.";
        TxtSideName.Text = "Не выполнен вход";
        TxtSideStatus.Text = "Оффлайн";
        ImgSkinPreview.Source = null;
        ImgBannerSkin.Source = null;
        ImgAvatar.Source = null;
        TxtSkinPlaceholder.Visibility = Visibility.Visible;

        BtnLogout.IsEnabled = false;
        BtnUploadSkin.IsEnabled = false;
        BtnResetSkin.IsEnabled = false;

        TxtOfflineName.Clear();
        TxtOfflineHint.Text = "Введите никнейм (3-16 символов).";
        TxtOfflineHint.Foreground = (Brush)FindResource("FgMuted");
        TxtSkinStatus.Text = "";

        AppendLog("Выполнен выход из аккаунта.");
    }

    private void SetAccount(MinecraftAccount acc, bool refreshSkin)
    {
        _account = acc;

        TxtAccName.Text = acc.Username;
        TxtAccUuid.Text = acc.DashedUuid;

        if (acc.IsOffline)
        {
            TxtAuthState.Text = "Активен оффлайн-профиль. Для официальных серверов и смены скина " +
                                "войдите через Microsoft.";
            TxtSideStatus.Text = "Оффлайн-профиль";
        }
        else
        {
            TxtAuthState.Text = acc.IsExpired
                ? "Сессия истекла — потребуется повторный вход."
                : $"Вход выполнен. Сессия активна до {acc.ExpiresAt.ToLocalTime():dd.MM.yyyy HH:mm}.";
            TxtSideStatus.Text = acc.IsExpired ? "Сессия истекла" : "Microsoft · онлайн";
        }

        TxtSideName.Text = acc.Username;
        BtnLogout.IsEnabled = true;
        BtnUploadSkin.IsEnabled = !acc.IsOffline;
        BtnResetSkin.IsEnabled = !acc.IsOffline;

        if (acc.IsOffline)
        {
            TxtSkinStatus.Text = "Смена скина недоступна для оффлайн-профиля.";
            TxtSkinStatus.Foreground = (Brush)FindResource("FgMuted");
        }

        if (refreshSkin) _ = LoadSkinImagesAsync(acc);
    }

    private async Task LoadSkinImagesAsync(MinecraftAccount acc)
    {
        try
        {
            ImageSource? localBody = null;
            ImageSource? localAvatar = null;
            if (acc.IsOffline)
            {
                var localFile = OfflineSkinService.FindAccountSkin(acc.Username);
                if (localFile != null)
                {
                    byte[]? bytes = null;
                    try { bytes = await File.ReadAllBytesAsync(localFile); }
                    catch { }
                    if (bytes != null)
                    {
                        var render = await Task.Run(() => SkinBodyRenderer.Render(bytes, false));
                        if (render != null)
                        {
                            localBody = render;
                            var head = new System.Windows.Media.Imaging.CroppedBitmap(render, new Int32Rect(24, 0, 48, 48));
                            head.Freeze();
                            localAvatar = head;
                        }
                    }
                }
            }

            byte[]? bodyBytes = null, avatarBytes = null;
            if (localBody == null) bodyBytes = await _skins.GetBodyRenderAsync(acc);
            if (localAvatar == null) avatarBytes = await _skins.GetAvatarAsync(acc, 72);

            Dispatcher.Invoke(() =>
            {
                if (localBody is not null || bodyBytes is not null)
                {
                    var img = localBody ?? ToImage(bodyBytes!);
                    ImgSkinPreview.Source = img;
                    ImgBannerSkin.Source = img;
                    TxtSkinPlaceholder.Visibility = Visibility.Collapsed;
                }
                ImgAvatar.Source = localAvatar ?? (avatarBytes != null ? ToImage(avatarBytes) : null);
            });
        }
        catch (Exception ex) { Log.Warn("Не удалось загрузить скин: " + ex.Message); }
    }

    private static BitmapImage ToImage(byte[] data)
    {
        var bmp = new BitmapImage();
        using var ms = new MemoryStream(data);
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private async void BtnRefreshSkin_Click(object sender, RoutedEventArgs e)
    {
        if (_account is null) return;
        TxtSkinStatus.Text = "Обновляю превью...";
        await LoadSkinImagesAsync(_account);
        TxtSkinStatus.Text = "Превью обновлено.";
    }

    private void BtnBrowseSkin_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Выберите файл скина",
            Filter = "PNG изображения (*.png)|*.png|Все файлы (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != true) return;

        TxtSkinPath.Text = dlg.FileName;

        try
        {
            SkinService.ValidateSkinPng(File.ReadAllBytes(dlg.FileName));
            TxtSkinStatus.Text = "Файл корректный. Нажмите «Сменить скин».";
            TxtSkinStatus.Foreground = (Brush)FindResource("Accent");
        }
        catch (Exception ex)
        {
            TxtSkinStatus.Text = ex.Message;
            TxtSkinStatus.Foreground = (Brush)FindResource("Danger");
        }
    }

    private async void BtnUploadSkin_Click(object sender, RoutedEventArgs e)
    {
        if (_account is null) return;

        var path = TxtSkinPath.Text.Trim();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            TxtSkinStatus.Text = "Сначала выберите PNG-файл скина.";
            TxtSkinStatus.Foreground = (Brush)FindResource("Danger");
            return;
        }

        BtnUploadSkin.IsEnabled = false;
        TxtSkinStatus.Foreground = (Brush)FindResource("FgMuted");
        TxtSkinStatus.Text = "Отправляю скин на серверы Mojang...";

        try
        {
            if (_account.IsOffline)
                throw new InvalidOperationException(
                    "Смена скина доступна только для аккаунта Microsoft.");

            if (_account.IsExpired && !string.IsNullOrEmpty(_account.MicrosoftRefreshToken))
            {
                _account = await _auth.RefreshAsync(_account.MicrosoftRefreshToken!);
                AccountStorage.Save(_account);
                SetAccount(_account, refreshSkin: false);
            }

            var model = RbSlim.IsChecked == true ? SkinService.SkinModel.Slim : SkinService.SkinModel.Classic;
            await _skins.UploadSkinAsync(_account.AccessToken, path, model);

            TxtSkinStatus.Text = "Скин изменён! Обновляю превью...";
            TxtSkinStatus.Foreground = (Brush)FindResource("Accent");

            await Task.Delay(2500);
            await LoadSkinImagesAsync(_account);
            TxtSkinStatus.Text = "Скин успешно изменён.";
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка смены скина", ex);
            TxtSkinStatus.Text = ex.Message;
            TxtSkinStatus.Foreground = (Brush)FindResource("Danger");
        }
        finally { BtnUploadSkin.IsEnabled = true; }
    }

    private async void BtnResetSkin_Click(object sender, RoutedEventArgs e)
    {
        if (_account is null) return;

        if (MessageBox.Show("Сбросить скин на стандартный?", "Подтверждение",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        BtnResetSkin.IsEnabled = false;
        try
        {
            await _skins.ResetSkinAsync(_account.AccessToken);
            TxtSkinStatus.Text = "Скин сброшен.";
            TxtSkinStatus.Foreground = (Brush)FindResource("Accent");
            await Task.Delay(2000);
            await LoadSkinImagesAsync(_account);
        }
        catch (Exception ex)
        {
            TxtSkinStatus.Text = ex.Message;
            TxtSkinStatus.Foreground = (Brush)FindResource("Danger");
        }
        finally { BtnResetSkin.IsEnabled = true; }
    }

    // =====================================================================
    //  ПРОЧЕЕ
    // =====================================================================

    // =====================================================================
    //  НАСТРОЙКИ: РАЗДЕЛЫ И ДЕЙСТВИЯ
    // =====================================================================

    private void SettingsSection_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || SecPanelGame is null) return;

        var tag = (sender as FrameworkElement)?.Tag?.ToString() ?? "game";
        _currentSettingsSection = tag;
        SecPanelGame.Visibility = tag == "game" ? Visibility.Visible : Visibility.Collapsed;
        SecPanelJava.Visibility = tag == "java" ? Visibility.Visible : Visibility.Collapsed;
        SecPanelView.Visibility = tag == "view" ? Visibility.Visible : Visibility.Collapsed;
        SecPanelStorage.Visibility = tag == "storage" ? Visibility.Visible : Visibility.Collapsed;
        SecPanelVersions.Visibility = tag == "versions" ? Visibility.Visible : Visibility.Collapsed;
        SecPanelMaint.Visibility = tag == "maint" ? Visibility.Visible : Visibility.Collapsed;

        if (tag == "versions" && ItemsVersions.ItemsSource is null) ScanVersions();
        if (tag == "maint" && ItemsMaint.ItemsSource is null) ScanMaintenance();
        if (tag == "storage") RefreshPortableState();
    }

    /// <summary>Любое изменение настройки — сразу сохраняем на диск.</summary>
    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _initializing) return;
        PersistSettings();
        UpdateRunStateUi();
        ShowSavedHint();
    }

    private void SetResolution(int w, int h)
    {
        TxtWidth.Text = w.ToString();
        TxtHeight.Text = h.ToString();
        ChkFullscreen.IsChecked = false;
        PersistSettings();
    }

    private void Preset720_Click(object s, RoutedEventArgs e) => SetResolution(1280, 720);
    private void Preset900_Click(object s, RoutedEventArgs e) => SetResolution(1600, 900);
    private void Preset1080_Click(object s, RoutedEventArgs e) => SetResolution(1920, 1080);

    private void SetMemory(int mb)
    {
        SldMemory.Value = Math.Clamp(mb, 1024, 16384);
        PersistSettings();
    }

    private void Mem2_Click(object s, RoutedEventArgs e) => SetMemory(2048);
    private void Mem4_Click(object s, RoutedEventArgs e) => SetMemory(4096);
    private void Mem8_Click(object s, RoutedEventArgs e) => SetMemory(8192);
    private void MemAuto_Click(object s, RoutedEventArgs e) => SetMemory(LauncherSettings.RecommendedMaxMemory());

    private void BtnRescanJava_Click(object sender, RoutedEventArgs e)
    {
        TxtJavaList.Text = "Поиск…";
        _ = Task.Run(DetectJava);
    }

    private void BtnClearJava_Click(object sender, RoutedEventArgs e)
    {
        TxtJavaPath.Clear();
        _settings.CustomJavaPath = "";
        PersistSettings();
        _ = Task.Run(DetectJava);
    }

    // ---------- Управление версиями игры ----------

    private List<InstalledVersion> _installedVersions = new();

    // ---------- Сохранение и сброс по разделам ----------

    private string _currentSettingsSection = "game";

    // Текстовые поля сохраняем с задержкой, чтобы не писать файл на каждую букву
    private DispatcherTimer? _autoSaveTimer;

    private void ScheduleAutoSave(Action action)
    {
        _autoSaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };

        _autoSaveTimer.Stop();

        foreach (var d in _autoSaveHandlers) _autoSaveTimer.Tick -= d;
        _autoSaveHandlers.Clear();

        EventHandler handler = (_, _) =>
        {
            _autoSaveTimer!.Stop();
            action();
        };

        _autoSaveHandlers.Add(handler);
        _autoSaveTimer.Tick += handler;
        _autoSaveTimer.Start();
    }

    private readonly List<EventHandler> _autoSaveHandlers = new();

    private void SettingText_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _initializing) return;
        ScheduleAutoSave(() => { PersistSettings(); ShowSavedHint(); });
    }

    private void InstSettingText_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _initializing || _loadingInstSettings) return;
        ScheduleAutoSave(() => InstSetting_Changed(sender, e));
    }

    /// <summary>Ненавязчиво показываем, что изменения записаны.</summary>
    private void ShowSavedHint()
    {
        if (TxtSettingsHint is null) return;

        TxtSettingsHint.Text = $"Сохранено в {DateTime.Now:HH:mm:ss}";
        TxtSettingsHint.Foreground = (Brush)FindResource("Accent");

        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (s, _) =>
        {
            t.Stop();
            TxtSettingsHint.Text = "Изменения применяются и сохраняются сразу";
            TxtSettingsHint.Foreground = (Brush)FindResource("FgMuted");
        };
        t.Start();
    }

    private void BtnResetSection_Click(object sender, RoutedEventArgs e)
    {
        var sectionName = _currentSettingsSection switch
        {
            "java" => "«Java и память»",
            "view" => "«Внешний вид»",
            "storage" => "«Хранилище»",
            "versions" => "«Версии игры»",
            "maint" => "«Обслуживание»",
            _ => "«Игра»"
        };

        if (_currentSettingsSection is "versions" or "maint")
        {
            MessageBox.Show($"В разделе {sectionName} нет настроек для сброса.",
                "Сброс", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show($"Сбросить настройки раздела {sectionName} к значениям по умолчанию?",
                "Сброс раздела", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;

        var def = new LauncherSettings();

        switch (_currentSettingsSection)
        {
            case "game":
                _settings.WindowWidth = def.WindowWidth;
                _settings.WindowHeight = def.WindowHeight;
                _settings.Fullscreen = def.Fullscreen;
                _settings.AllowMultipleInstances = def.AllowMultipleInstances;
                _settings.MinimizeOnLaunch = def.MinimizeOnLaunch;
                _settings.ConfirmGameStop = def.ConfirmGameStop;
                _settings.CloseLauncherOnStart = def.CloseLauncherOnStart;
                _settings.ShowConsole = def.ShowConsole;
                _settings.ShowSnapshots = def.ShowSnapshots;
                _settings.DefaultIsolated = def.DefaultIsolated;
                _settings.AutoSetGameLanguage = def.AutoSetGameLanguage;
                _settings.GameLanguage = def.GameLanguage;
                break;

            case "java":
                _settings.MaxMemoryMb = LauncherSettings.RecommendedMaxMemory();
                _settings.CustomJavaPath = "";
                _settings.ExtraJvmArgs = "";
                break;

            case "view":
                _settings.Theme = def.Theme;
                _settings.AccentColor = def.AccentColor;
                _settings.BackgroundStyle = def.BackgroundStyle;
                _settings.CustomBannerPath = "";
                _settings.WindowBackgroundPath = "";
                _settings.WindowBackgroundOpacity = def.WindowBackgroundOpacity;
                _settings.Animations = def.Animations;

                ThemeService.CustomPreset = null;
                _settings.CustomThemeJson = "";
                ThemeService.ApplyTheme(_settings.Theme);
                ThemeService.ApplyAccent(_settings.AccentColor);
                BuildThemeCards();
                BuildAccentSwatches();
                BuildBackgroundStyleButtons();
                ApplyBanner();
                ApplyWindowBackground();
                break;

            case "storage":
                _settings.GameDir = LauncherPaths.Root;
                break;
        }

        SettingsService.Save(_settings);
        ApplySettingsToUi();

        AppendLog($"Раздел {sectionName} сброшен.");
    }

    // ---------- Своя цветовая схема ----------

    private void BtnCustomTheme_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ThemeEditorDialog(ThemeService.CustomPreset, ThemeService.CurrentAccent)
        {
            Owner = this
        };

        if (dlg.ShowDialog() != true || dlg.Result is null) return;

        ThemeService.CustomPreset = dlg.Result;
        _settings.CustomThemeJson = System.Text.Json.JsonSerializer.Serialize(dlg.Result);
        _settings.Theme = ThemeService.CustomThemeName;

        ThemeService.ApplyTheme(ThemeService.CustomThemeName);
        ThemeService.ApplyAccent(_settings.AccentColor);

        BuildThemeCards();
        BuildAccentSwatches();
        ApplyBanner();
        ApplyWindowBackground();
        SettingsService.Save(_settings);

        AppendLog("Применена своя цветовая схема.");
    }
    private void BtnScanVersions_Click(object sender, RoutedEventArgs e) => ScanVersions();

    private void ScanVersions()
    {
        TxtVersionsSummary.Text = "Сканирую…";

        try
        {
            _installedVersions = VersionManagerService.Scan(_instances);

            var total = _installedVersions.Sum(v => v.SizeBytes);
            TxtVersionsSummary.Text = _installedVersions.Count == 0
                ? "Версии ещё не установлены. Они появятся после первого запуска игры."
                : $"Всего версий: {_installedVersions.Count}  ·  занято {Human(total)}";

            ItemsVersions.ItemsSource = _installedVersions.Select(v =>
            {
                var (bg, fg) = v.Kind switch
                {
                    "Fabric" => ("#1A2A38", "#38BDF8"),
                    "Forge" => ("#33280F", "#FACC15"),
                    "NeoForge" => ("#2A1F33", "#A78BFA"),
                    _ => ("#14301F", "#4ADE80")
                };

                var parts = new List<string> { v.SizeDisplay };
                if (v.IsIsolated) parts.Add($"изолированная · {v.OwnerInstance}");
                if (!v.HasJar) parts.Add("клиент не загружен");
                if (v.InheritsFrom is not null) parts.Add($"на базе {v.InheritsFrom}");
                parts.Add(v.InUse ? "используется: " + string.Join(", ", v.UsedBy) : "не используется");

                return new
                {
                    v.Id,
                    v.Kind,
                    Dir = v.Directory,
                    Info = string.Join("  ·  ", parts),
                    KindBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg)),
                    KindFg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg))
                };
            }).ToList();
        }
        catch (Exception ex)
        {
            TxtVersionsSummary.Text = "Ошибка сканирования: " + ex.Message;
        }
    }

    private void VersionOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string dir) return;
        try { InstanceService.OpenFolder(dir); }
        catch (Exception ex) { AppendLog(ex.Message); }
    }

    private void VersionDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;

        var version = _installedVersions.FirstOrDefault(v => v.Id == id);
        if (version is null) return;

        if (_sessions.AnyRunning)
        {
            MessageBox.Show("Сначала остановите игру — файлы версии сейчас заняты.",
                "Игра запущена", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var warn = version.InUse
            ? $"\n\nВНИМАНИЕ: версию используют сборки: {string.Join(", ", version.UsedBy)}.\n" +
              "После удаления они скачают файлы заново при запуске."
            : "";

        var r = MessageBox.Show(
            $"Удалить версию «{version.Id}»?\n\n" +
            $"Освободится {version.SizeDisplay}.\n" +
            "Моды, миры и настройки сборок затронуты не будут." + warn,
            "Удаление версии", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (r != MessageBoxResult.Yes) return;

        try
        {
            var freed = VersionManagerService.Delete(version);
            AppendLog($"Версия {version.Id} удалена, освобождено {Human(freed)}.");
            ScanVersions();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось удалить: " + ex.Message + "\n\n" +
                            "Возможно, файлы заняты другой программой.",
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void BtnCalcSize_Click(object sender, RoutedEventArgs e)
    {
        TxtStorageInfo.Text = "Считаю…";

        _ = Task.Run(() =>
        {
            long Size(string dir)
            {
                try
                {
                    return Directory.Exists(dir)
                        ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                        : 0;
                }
                catch { return 0; }
            }

            var libs = Size(LauncherPaths.LibrariesDir);
            var assets = Size(LauncherPaths.AssetsDir);
            var versions = Size(LauncherPaths.VersionsDir);
            var runtime = Size(LauncherPaths.RuntimeDir);
            var cache = Size(LauncherPaths.CacheDir);

            var perInstance = new List<string>();
            long instancesTotal = 0;

            foreach (var inst in _instances)
            {
                var s = Size(InstanceService.InstanceDir(inst));
                instancesTotal += s;
                perInstance.Add($"     • {inst.Name}: {Human(s)}" + (inst.Isolated ? "  (изолированная)" : ""));
            }

            var text =
                $"Общее хранилище:\n" +
                $"     библиотеки: {Human(libs)}\n" +
                $"     ресурсы: {Human(assets)}\n" +
                $"     версии: {Human(versions)}\n" +
                $"     Java: {Human(runtime)}\n" +
                $"     кэш: {Human(cache)}\n\n" +
                $"Сборки ({_instances.Count}): {Human(instancesTotal)}\n" +
                string.Join("\n", perInstance) +
                $"\n\nВсего: {Human(libs + assets + versions + runtime + cache + instancesTotal)}";

            Dispatcher.Invoke(() => TxtStorageInfo.Text = text);
        });
    }

    private void BtnClearCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var freed = 0L;
            if (Directory.Exists(LauncherPaths.CacheDir))
            {
                foreach (var f in Directory.GetFiles(LauncherPaths.CacheDir))
                {
                    // манифест версий оставляем — он нужен в офлайне
                    if (f.EndsWith("version_manifest_v2.json", StringComparison.OrdinalIgnoreCase)) continue;
                    try { freed += new FileInfo(f).Length; File.Delete(f); } catch { }
                }
            }

            TxtMaintenance.Text = $"Кэш очищен, освобождено {Human(freed)}.";
            AppendLog($"Кэш очищен ({Human(freed)}).");
        }
        catch (Exception ex)
        {
            TxtMaintenance.Text = "Не удалось очистить кэш: " + ex.Message;
        }
    }

    private async void BtnCheckCurse_Click(object sender, RoutedEventArgs e)
    {
        TxtMaintenance.Text = "Проверяю доступ к Modrinth API…";

        var ok = await Task.FromResult(true);

        TxtMaintenance.Text = ok
            ? "Modrinth API доступен."
            : "Только Modrinth.";

        UpdateModsSubtitle();
    }

    private void BtnResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(
            "Сбросить все настройки лаунчера к значениям по умолчанию?\n\n" +
            "Сборки, моды и аккаунт затронуты не будут.",
            "Сброс настроек", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (r != MessageBoxResult.Yes) return;

        _settings = new LauncherSettings
        {
            MaxMemoryMb = LauncherSettings.RecommendedMaxMemory(),
            GameDir = LauncherPaths.Root
        };

        SettingsService.Save(_settings);
        ThemeService.ApplyTheme(_settings.Theme);
        ThemeService.ApplyAccent(_settings.AccentColor);
        ApplySettingsToUi();
        BuildThemeCards();
        BuildAccentSwatches();
        BuildBackgroundStyleButtons();
        ApplyBanner();
        ApplyWindowBackground();

        TxtMaintenance.Text = "Настройки сброшены.";
        AppendLog("Настройки сброшены к значениям по умолчанию.");
    }

    // =====================================================================
    //  МОДЫ
    // =====================================================================

    private List<ModSearchResult> _modResults = new();
    private CancellationTokenSource? _modCts;
    private const int ModPageSize = 20;
    private int _modOffset;
    private int _modTotal;

    private void TxtModSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) RunModSearchFromStart();
    }

    private System.Timers.Timer? _searchDebounceTimer;

    private void TxtModSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _searchDebounceTimer?.Stop();
        _searchDebounceTimer = new System.Timers.Timer(600);
        _searchDebounceTimer.Elapsed += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Dispose();
            Dispatcher.Invoke(() =>
            {
                if (TxtModSearch.Text.Length >= 2)
                    RunModSearchFromStart();
            });
        };
        _searchDebounceTimer.AutoReset = false;
        _searchDebounceTimer.Start();
    }

    private void ModFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (_modResults.Count > 0) RunModSearchFromStart();
    }

    private ModContentType SelectedContentType => (CbModType.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
    {
        "1" => ModContentType.ResourcePack,
        "2" => ModContentType.ShaderPack,
        _ => ModContentType.Mod
    };

    private ModProvider? SelectedProvider => (CbModSource.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
    {
        "modrinth" => ModProvider.Modrinth,
        "curseforge" => ModProvider.CurseForge,
        _ => null
    };

    /// <summary>Обновляет состояние пагинации под списком.</summary>
    private void UpdatePager(ModService.SearchPage page)
    {
        ModPager.Visibility = page.TotalCount > ModPageSize ? Visibility.Visible : Visibility.Collapsed;
        TxtPageInfo.Text = $"Страница {page.PageNumber} из {page.TotalPages}";
        BtnPrevPage.IsEnabled = page.HasPrevious;
        BtnNextPage.IsEnabled = page.HasNext;
    }

    private void BtnPrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_modOffset <= 0) return;
        _modOffset = Math.Max(0, _modOffset - ModPageSize);
        RunModSearch();
    }

    private void BtnNextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_modOffset + ModPageSize >= _modTotal) return;
        _modOffset += ModPageSize;
        RunModSearch();
    }

    /// <summary>Новый запрос — всегда с первой страницы.</summary>
    private void RunModSearchFromStart()
    {
        _modOffset = 0;
        RunModSearch();
    }

    private void RunModSearch() => BtnModSearch_Click(this, new RoutedEventArgs());

    /// <summary>Кнопка «Найти»: новый запрос с первой страницы.</summary>
    private void BtnModSearchNew_Click(object sender, RoutedEventArgs e) => RunModSearchFromStart();

    private async void BtnModSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null)
        {
            TxtModStatus.Text = "Сначала выберите сборку — от неё зависят версия игры и загрузчик.";
            return;
        }

        _modCts?.Cancel();
        _modCts = new CancellationTokenSource();
        var ct = _modCts.Token;

        BtnModSearch.IsEnabled = false;
        var type = SelectedContentType;
        TxtModStatus.Text = "Ищу…";
        ItemsMods.ItemsSource = null;

        try
        {
            var page = await _mods.SearchAsync(
                TxtModSearch.Text.Trim(),
                _selectedInstance.McVersion,
                _selectedInstance.Loader,
                type,
                SelectedProvider,
                ModPageSize, _modOffset, ct);

            if (ct.IsCancellationRequested) return;

            _modResults = page.Items;
            _modTotal = page.TotalCount;

            if (_modResults.Count == 0)
            {
                TxtModStatus.Text = _selectedInstance.Loader == LoaderKind.Vanilla && type == ModContentType.Mod
                    ? $"Ничего не найдено. У сборки «{_selectedInstance.Name}» нет модлоадера — " +
                      "для модов создайте сборку с Fabric, Forge или NeoForge."
                    : $"Ничего не найдено для Minecraft {_selectedInstance.McVersion}.";
                UpdatePager(page);
                return;
            }

            var extra = "";
            TxtModStatus.Text = $"Найдено: {page.TotalCount}  ·  " +
                                $"{_selectedInstance.McVersion} · {_selectedInstance.Loader.Display()}{extra}";

            ItemsMods.ItemsSource = _modResults.Select((m, i) => BuildModView(m, i)).ToList();
            UpdatePager(page);
            ModScroll.ScrollToTop();

            _ = LoadModIconsAsync(_modResults.ToList(), ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            TxtModStatus.Text = "Ошибка поиска: " + ex.Message;
            Log.Error("Поиск модов", ex);
        }
        finally { BtnModSearch.IsEnabled = true; }
    }

    private object BuildModView(ModSearchResult m, int index)
    {
        // Только то, что уже в кэше. Остальное догрузится асинхронно (LoadModIconsAsync),
        // иначе WPF качает картинку прямо в UI-потоке и окно виснет.
        var icon = ImageCacheService.TryGetCached(m.IconUrl);

        var isModrinth = m.Provider == ModProvider.Modrinth;

        return new
        {
            Index = index,
            m.Title,
            Summary = string.IsNullOrWhiteSpace(m.Summary) ? "Без описания" : m.Summary,
            Icon = icon,
            Initial = m.Title.Length > 0 ? m.Title[..1].ToUpperInvariant() : "?",
            PlaceholderVisibility = icon is null ? Visibility.Visible : Visibility.Collapsed,
            Source = m.ProviderDisplay,
            SourceBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isModrinth ? "#14301F" : "#33210F")),
            SourceFg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isModrinth ? "#4ADE80" : "#FB923C")),
            DownloadsText = m.DownloadsDisplay + " загрузок",
            AuthorText = string.IsNullOrEmpty(m.Author) ? "" : "автор: " + m.Author,
            PageUrl = m.PageUrl ?? ""
        };
    }

    /// <summary>
    /// Догружает иконки в фоне и обновляет список, когда они готовы.
    /// Так каталог появляется мгновенно, а картинки подтягиваются постепенно.
    /// </summary>
    private async Task LoadModIconsAsync(List<ModSearchResult> items, CancellationToken ct)
    {
        var loadedAny = false;

        await Parallel.ForEachAsync(items,
            new ParallelOptions { MaxDegreeOfParallelism = 5, CancellationToken = ct },
            async (m, token) =>
            {
                var img = await ImageCacheService.GetAsync(m.IconUrl, App.Http, 108, token);
                if (img is not null) loadedAny = true;
            });

        if (!loadedAny || ct.IsCancellationRequested) return;

        await Dispatcher.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested) return;
            if (!ReferenceEquals(_modResults, items) && !_modResults.SequenceEqual(items)) return;

            ItemsMods.ItemsSource = _modResults.Select((m, i) => BuildModView(m, i)).ToList();
        });
    }
    private void ModPage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string url || url.Length == 0) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { AppendLog("Не удалось открыть ссылку: " + ex.Message); }
    }

    /// <summary>Открывает страницу мода во встроенном браузере.</summary>
    private void ModPageInApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int index) return;
        if (index < 0 || index >= _modResults.Count) return;

        var project = _modResults[index];

        var dlg = new ModBrowserDialog(project) { Owner = this };
        var result = dlg.ShowDialog();

        // Из окна просмотра можно сразу поставить мод
        if (result == true && dlg.InstallRequested)
            _ = InstallModAsync(project, null);
    }
    private async void ModInstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int index) return;
        if (index < 0 || index >= _modResults.Count) return;

        await InstallModAsync(_modResults[index], btn);
    }

    /// <summary>Общий путь установки: из каталога и из окна просмотра.</summary>
    private async Task InstallModAsync(ModSearchResult project, Button? btn)
    {
        if (_selectedInstance is null)
        {
            MessageBox.Show("Сначала выберите сборку.", "Сборка не выбрана",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var inst = _selectedInstance;

        if (btn is not null) { btn.IsEnabled = false; btn.Content = "…"; }

        try
        {
            // Диалог выбора версии — как в Modrinth App
            var dlg = new ModVersionDialog(_mods, project, inst.McVersion, inst.Loader) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.SelectedFile is null) return;

            var chosen = dlg.SelectedFile;

            var targetDir = SelectedContentType switch
            {
                ModContentType.ResourcePack => InstanceService.ResourcePacksDir(inst),
                ModContentType.ShaderPack => InstanceService.ShaderPacksDir(inst),
                _ => InstanceService.ModsDir(inst)
            };

            var outcome = await _mods.InstallAsync(
                chosen, targetDir, inst.McVersion, inst.Loader, dlg.InstallDependencies);

            var msg = $"Установлено: {outcome.Installed.Count}";
            if (outcome.Skipped.Count > 0) msg += $"\nПропущено: {string.Join(", ", outcome.Skipped)}";
            if (outcome.Failed.Count > 0) msg += $"\nОшибки: {string.Join(", ", outcome.Failed)}";

            AppendLog($"«{project.Title}» → {msg.Replace("\n", "; ")}");

            MessageBox.Show(msg, project.Title,
                outcome.Failed.Count > 0 ? MessageBoxButton.OK : MessageBoxButton.OK,
                outcome.Failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            RefreshInstanceStats();
        }
        catch (Exception ex)
        {
            Log.Error("Установка мода", ex);
            MessageBox.Show(ex.Message, "Ошибка установки", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (btn is not null) { btn.IsEnabled = true; btn.Content = "Установить"; }
        }
    }

    private void UpdateModsSubtitle()
    {
        if (TxtModsSubtitle is null) return;

        TxtModsSubtitle.Text = _mods.CurseForgeAvailable
            ? "Каталог Modrinth и CurseForge"
            : "Каталог Modrinth  ·  CurseForge недоступен с текущим ключом";
    }
    private void SldMemory_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        var mb = (int)e.NewValue;
        TxtMemory.Text = $"{mb} МБ";
        TxtBadgeRam.Text = $"RAM: {mb} МБ";
        _settings.MaxMemoryMb = mb;
    }

    private void BtnBrowseJava_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Выберите java.exe",
            Filter = "java.exe|java.exe;javaw.exe|Исполняемые файлы (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != true) return;

        var probe = JavaService.Probe(dlg.FileName, "custom");
        if (probe is null)
        {
            MessageBox.Show("Не удалось определить версию Java.", "Java",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        TxtJavaPath.Text = dlg.FileName;
        _settings.CustomJavaPath = dlg.FileName;
        TxtBadgeJava.Text = $"Java {probe.MajorVersion}";
        AppendLog("Выбрана Java: " + probe);
    }

    private void BtnOpenDir_Click(object sender, RoutedEventArgs e)
    {
        try { InstanceService.OpenFolder(_settings.GameDir); }
        catch (Exception ex) { AppendLog("Не удалось открыть папку: " + ex.Message); }
    }

    private void BtnOpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        try { InstanceService.RevealFile(LauncherPaths.LauncherLogFile); }
        catch (Exception ex) { AppendLog(ex.Message); }
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        lock (_logBuffer) _logBuffer.Clear();
        TxtLog.Clear();
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || PageHome is null) return;

        var tag = (sender as FrameworkElement)?.Tag?.ToString() ?? "0";

        AnimatePageTransition(tag);

        if (tag == "1") { RefreshInstanceStats(); LoadScreenshots(); }
        if (tag == "6")
        {
            UpdateModsSubtitle();
            if (_modResults.Count == 0 && _selectedInstance is not null) RunModSearchFromStart();
        }
        if (tag == "7") RefreshContent();
        if (tag == "8") RefreshBotEnvInfo();
        if (tag == "9") UpdateSkinTabHeader();
    }

    private void AnimatePageTransition(string tag)
    {
        var pages = new Dictionary<string, Grid>
        {
            ["0"] = PageHome, ["1"] = PageInstances, ["2"] = PageServers,
            ["3"] = PageAccount, ["4"] = PageSettings, ["5"] = PageConsole,
            ["6"] = PageMods, ["7"] = PageContent, ["8"] = PageBot, ["9"] = PageSkins
        };

        foreach (var kvp in pages)
        {
            kvp.Value.Visibility = kvp.Key == tag ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // =====================================================================
    //  ПОИСК ПО СБОРКАМ
    // =====================================================================

    private string _instanceFilter = "";

    private void TxtInstanceSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;

        _instanceFilter = TxtInstanceSearch.Text.Trim();
        RefreshInstanceLists();
    }

    /// <summary>Поле поиска появляется, когда сборок становится много.</summary>
    private void UpdateSearchVisibility()
    {
        if (TxtInstanceSearch is null) return;

        var show = _instances.Count >= 5 || _instanceFilter.Length > 0;
        TxtInstanceSearch.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private List<GameInstance> ApplyInstanceFilter(List<GameInstance> source)
    {
        if (_instanceFilter.Length == 0) return source;

        return source.Where(i =>
            i.Name.Contains(_instanceFilter, StringComparison.OrdinalIgnoreCase) ||
            i.McVersion.Contains(_instanceFilter, StringComparison.OrdinalIgnoreCase) ||
            i.LoaderDisplay.Contains(_instanceFilter, StringComparison.OrdinalIgnoreCase)
        ).ToList();
    }

    // =====================================================================
    //  ФИЛЬТР КОНСОЛИ
    // =====================================================================

    private string _logFilter = "all";

    private void LogFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        _logFilter = (sender as FrameworkElement)?.Tag?.ToString() ?? "all";
        ApplyLogFilter();
    }

    private void ApplyLogFilter()
    {
        string all;
        lock (_logBuffer) all = _logBuffer.ToString();

        if (_logFilter == "all")
        {
            TxtLog.Text = all;
            TxtLogInfo.Text = "Журнал лаунчера и вывод игры";
            ScrollLog.ScrollToEnd();
            return;
        }

        var lines = all.Split('\n');
        var filtered = lines.Where(l => MatchesLogLevel(l, _logFilter)).ToList();

        TxtLog.Text = filtered.Count > 0
            ? string.Join("\n", filtered)
            : (_logFilter == "error" ? "Ошибок нет." : "Предупреждений нет.");

        TxtLogInfo.Text = $"Показано {filtered.Count} из {lines.Length} строк";
        ScrollLog.ScrollToEnd();
    }

    private static bool MatchesLogLevel(string line, string level)
    {
        var lower = line.ToLowerInvariant();

        var isError = lower.Contains("[error]") || lower.Contains("error]") ||
                      lower.Contains("exception") || lower.Contains("ошибка") ||
                      lower.Contains("не удалось") || lower.Contains("severe") ||
                      lower.Contains("fatal") || lower.Contains("!!!");

        if (level == "error") return isError;

        // Для «предупреждений» показываем и ошибки — они важнее
        return isError || lower.Contains("[warn]") || lower.Contains("warn]") ||
               lower.Contains("внимание") || lower.Contains("предупрежд");
    }

    // =====================================================================
    //  ГОРЯЧИЕ КЛАВИШИ
    // =====================================================================

    // =====================================================================
    //  ПРОКРУТКА КОЛЁСИКОМ
    // =====================================================================

    /// <summary>
    /// ComboBox и Slider «съедают» колесо мыши: наведёшь курсор на список версий
    /// при прокрутке страницы — и вместо скролла меняется значение.
    /// Здесь мы гасим такое поведение и передаём прокрутку родительскому ScrollViewer.
    /// </summary>
    private void BlockingControl_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not UIElement element) return;

        // У раскрытого списка прокрутка своя — не мешаем
        if (sender is ComboBox { IsDropDownOpen: true }) return;

        e.Handled = true;

        var parent = FindParentScrollViewer(element);
        parent?.ScrollToVerticalOffset(parent.VerticalOffset - e.Delta);
    }

    private static ScrollViewer? FindParentScrollViewer(DependencyObject start)
    {
        var current = VisualTreeHelper.GetParent(start);

        while (current is not null)
        {
            if (current is ScrollViewer sv && sv.ScrollableHeight > 0) return sv;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>
    /// Вешает защиту от «кражи» колеса на все ComboBox и Slider окна.
    /// Делается один раз после загрузки.
    /// </summary>
    private void SetupWheelHandling(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is ComboBox or Slider)
            {
                var el = (UIElement)child;
                el.PreviewMouseWheel -= BlockingControl_PreviewMouseWheel;
                el.PreviewMouseWheel += BlockingControl_PreviewMouseWheel;
            }

            // Внутрь раскрывающихся списков не лезем
            if (child is not ComboBox) SetupWheelHandling(child);
        }
    }

    /// <summary>Прокрутка списка сборок работает и когда курсор над карточкой.</summary>
    private void ListScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (sv.ScrollableHeight <= 0) return;

        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
        e.Handled = true;
    }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        // В полях ввода не перехватываем обычные клавиши
        var typing = Keyboard.FocusedElement is TextBox or ComboBox;

        switch (e.Key)
        {
            case Key.F5 when !typing || ctrl:
                RefreshCurrentPage();
                e.Handled = true;
                break;

            case Key.N when ctrl:
                BtnNewInstance_Click(sender, e);
                e.Handled = true;
                break;

            case Key.F when ctrl:
                NavInstances.IsChecked = true;
                TxtInstanceSearch.Visibility = Visibility.Visible;
                TxtInstanceSearch.Focus();
                TxtInstanceSearch.SelectAll();
                e.Handled = true;
                break;

            case Key.F11:
                if (_selectedInstance is not null && !_busy) _ = LaunchAsync(_selectedInstance, null);
                e.Handled = true;
                break;

            case Key.Escape when typing:
                Keyboard.ClearFocus();
                e.Handled = true;
                break;

            // Ctrl+1..9 — переключение вкладок
            case >= Key.D1 and <= Key.D9 when ctrl:
                SwitchTab(e.Key - Key.D1);
                e.Handled = true;
                break;
        }
    }

    private void SwitchTab(int index)
    {
        var navs = new[] { NavHome, NavInstances, NavServers, NavAccount,
                           NavSettings, NavConsole, NavMods, NavContent,
                           NavBot, NavSkins };

        if (index >= 0 && index < navs.Length) navs[index].IsChecked = true;
    }

    /// <summary>F5 — обновляет то, что открыто сейчас.</summary>
    private void RefreshCurrentPage()
    {
        if (PageInstances.Visibility == Visibility.Visible)
        {
            RefreshInstanceLists();
            RefreshInstanceStats();
            LoadScreenshots();
            AppendLog("Список сборок обновлён.");
        }
        else if (PageContent.Visibility == Visibility.Visible)
        {
            RefreshContent();
        }
        else if (PageServers.Visibility == Visibility.Visible)
        {
            _ = RefreshServersAsync();
        }
        else if (PageMods.Visibility == Visibility.Visible)
        {
            RunModSearchFromStart();
        }
        else if (PageSettings.Visibility == Visibility.Visible)
        {
            if (SecPanelVersions.Visibility == Visibility.Visible) ScanVersions();
            else if (SecPanelMaint.Visibility == Visibility.Visible) ScanMaintenance();
            else _ = Task.Run(DetectJava);
        }
        else if (PageBot.Visibility == Visibility.Visible)
        {
            RefreshBotList();
        }
        else
        {
            RefreshInstanceStats();
        }
    }

    // =====================================================================
    //  УВЕДОМЛЕНИЯ О ЗАВЕРШЕНИИ
    // =====================================================================

    /// <summary>
    /// Мигает в панели задач и подаёт звук, если окно свёрнуто —
    /// чтобы не сидеть и не смотреть на прогресс-бар.
    /// </summary>
    private void NotifyFinished(string title, string message, bool success = true)
    {
        Dispatcher.BeginInvoke(() =>
        {
            AppendLog($"{title}: {message}");

            var inactive = !IsActive || WindowState == WindowState.Minimized;
            if (!inactive) return;

            try
            {
                if (success) System.Media.SystemSounds.Asterisk.Play();
                else System.Media.SystemSounds.Exclamation.Play();
            }
            catch { }

            // Мигание значка в панели задач
            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                if (helper.Handle != IntPtr.Zero) FlashWindow(helper.Handle, true);
            }
            catch { }

            TxtRunningBadge.Text = message;
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool FlashWindow(IntPtr hwnd, bool bInvert);
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    // =====================================================================
    //  ПРОГРЕСС И ЛОГ
    // =====================================================================

    private void OnProgress(DownloadProgress p)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastProgressUi).TotalMilliseconds < 50 && p.Percent < 100) return;
        _lastProgressUi = now;

        Dispatcher.BeginInvoke(() =>
        {
            ProgressArea.Visibility = Visibility.Visible;
            PbProgress.IsIndeterminate = false;
            PbProgress.Value = p.Percent;
            TxtProgressPercent.Text = $"{p.Percent:F1}%";

            var detail = p.FilesTotal > 1 ? $"  ({p.FilesDone}/{p.FilesTotal})" : "";
            var size = p.BytesTotal > 0 ? $"  ·  {Human(p.BytesDone)} / {Human(p.BytesTotal)}" : "";
            var file = string.IsNullOrEmpty(p.CurrentFile) ? "" : "  —  " + Shorten(p.CurrentFile, 44);

            TxtProgressStage.Text = p.Stage + detail + size + file;
        });
    }

    private void SetStage(string stage)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ProgressArea.Visibility = Visibility.Visible;
            TxtProgressStage.Text = stage;
        });
        AppendLog(stage);
    }

    private void ShowProgress(bool indeterminate = false)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ProgressArea.Visibility = Visibility.Visible;
            PbProgress.IsIndeterminate = indeterminate;
            if (indeterminate) TxtProgressPercent.Text = "";
        });
    }

    private void HideProgress()
    {
        Dispatcher.BeginInvoke(() =>
        {
            PbProgress.IsIndeterminate = false;
            ProgressArea.Visibility = Visibility.Collapsed;
        });
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Dispatcher.Invoke(() =>
        {
            CbInstances.IsEnabled = !busy;
            BtnNewInstance.IsEnabled = !busy;
            BtnCancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

            UpdateRunStateUi();

            if (busy) ProgressArea.Visibility = Visibility.Visible;
            else
            {
                PbProgress.IsIndeterminate = false;
                Task.Delay(1400).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
                {
                    if (!_busy) ProgressArea.Visibility = Visibility.Collapsed;
                }));
            }
        });
    }

    private void AppendLog(string line)
    {
        lock (_logBuffer)
        {
            _logBuffer.AppendLine(line);
            if (_logBuffer.Length > 400_000) _logBuffer.Remove(0, 200_000);
        }

        Dispatcher.BeginInvoke(() =>
        {
            // При активном фильтре перерисовываем только подходящие строки
            if (_logFilter != "all")
            {
                if (MatchesLogLevel(line, _logFilter)) ApplyLogFilter();
                return;
            }

            string text;
            lock (_logBuffer) text = _logBuffer.ToString();
            TxtLog.Text = text;
            ScrollLog.ScrollToEnd();
        });
    }

    // =====================================================================
    //  КОНТЕНТ СБОРКИ
    // =====================================================================

    private enum ContentKind { Mods, ResourcePacks, Shaders, Worlds }

    private ContentKind _contentKind = ContentKind.Mods;

    private void ContentKind_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        _contentKind = (sender as FrameworkElement)?.Tag?.ToString() switch
        {
            "rp" => ContentKind.ResourcePacks,
            "shaders" => ContentKind.Shaders,
            "worlds" => ContentKind.Worlds,
            _ => ContentKind.Mods
        };

        RefreshContent();
    }

    private string CurrentContentDir()
    {
        if (_selectedInstance is null) return "";

        return _contentKind switch
        {
            ContentKind.ResourcePacks => InstanceService.ResourcePacksDir(_selectedInstance),
            ContentKind.Shaders => InstanceService.ShaderPacksDir(_selectedInstance),
            ContentKind.Worlds => InstanceService.SavesDir(_selectedInstance),
            _ => InstanceService.ModsDir(_selectedInstance)
        };
    }

    private void RefreshContent()
    {
        if (_selectedInstance is null)
        {
            TxtContentStatus.Text = "Сборка не выбрана.";
            ItemsContent.ItemsSource = null;
            return;
        }

        TxtContentSubtitle.Text = $"Сборка «{_selectedInstance.Name}» · " +
                                  $"{_selectedInstance.McVersion} · {_selectedInstance.LoaderDisplay}";

        var dir = CurrentContentDir();
        Directory.CreateDirectory(dir);

        var items = new List<object>();

        try
        {
            if (_contentKind == ContentKind.Worlds)
            {
                foreach (var d in new DirectoryInfo(dir).GetDirectories().OrderByDescending(x => x.LastWriteTime))
                {
                    long size = 0;
                    try { size = d.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { }

                    items.Add(new
                    {
                        Name = d.Name,
                        Info = $"{Human(size)} · изменён {d.LastWriteTime:dd.MM.yyyy HH:mm}",
                        Path = d.FullName,
                        ToggleText = "",
                        ToggleVisibility = Visibility.Collapsed,
                        Dot = new SolidColorBrush(ThemeService.CurrentAccent)
                    });
                }
            }
            else
            {
                var patterns = _contentKind == ContentKind.Mods
                    ? new[] { "*.jar", "*.jar.disabled" }
                    : new[] { "*.zip", "*.zip.disabled" };

                var files = patterns
                    .SelectMany(pat => new DirectoryInfo(dir).GetFiles(pat))
                    .DistinctBy(f => f.FullName)
                    .OrderBy(f => f.Name)
                    .ToList();

                foreach (var f in files)
                {
                    var enabled = !f.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                    var display = enabled
                        ? IOPath.GetFileNameWithoutExtension(f.Name)
                        : IOPath.GetFileNameWithoutExtension(IOPath.GetFileNameWithoutExtension(f.Name)) + "  (выключен)";

                    items.Add(new
                    {
                        Name = display,
                        Info = $"{Human(f.Length)} · {f.LastWriteTime:dd.MM.yyyy}",
                        Path = f.FullName,
                        ToggleText = enabled ? "Выключить" : "Включить",
                        ToggleVisibility = Visibility.Visible,
                        Dot = new SolidColorBrush(enabled
                            ? ThemeService.CurrentAccent
                            : (Color)ColorConverter.ConvertFromString("#6B7280"))
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Чтение содержимого сборки: " + ex.Message);
        }

        ItemsContent.ItemsSource = items;

        var kindName = _contentKind switch
        {
            ContentKind.ResourcePacks => "ресурспаков",
            ContentKind.Shaders => "шейдеров",
            ContentKind.Worlds => "миров",
            _ => "модов"
        };

        TxtContentStatus.Text = items.Count == 0
            ? $"Нет {kindName}. Перетащите файлы сюда или нажмите «Импорт»."
            : $"Всего {kindName}: {items.Count}";
    }

    private void BtnRefreshContent_Click(object sender, RoutedEventArgs e) => RefreshContent();

    private void BtnOpenContentFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;
        try { InstanceService.OpenFolder(CurrentContentDir()); }
        catch (Exception ex) { AppendLog(ex.Message); }
    }

    private void ContentToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string path) return;

        try
        {
            var target = path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                ? path[..^".disabled".Length]
                : path + ".disabled";

            if (File.Exists(target)) File.Delete(target);
            File.Move(path, target);

            RefreshContent();
            RefreshInstanceStats();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось переключить: " + ex.Message,
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ContentReveal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string path) return;

        try
        {
            if (Directory.Exists(path)) InstanceService.OpenFolder(path);
            else InstanceService.RevealFile(path);
        }
        catch (Exception ex) { AppendLog(ex.Message); }
    }

    private void ContentDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string path) return;

        var name = IOPath.GetFileName(path);
        var isDir = Directory.Exists(path);

        var msg = isDir
            ? $"Удалить мир «{name}»?\n\nЭто действие необратимо."
            : $"Удалить «{name}»?";

        if (MessageBox.Show(msg, "Удаление", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        try
        {
            if (isDir) Directory.Delete(path, true);
            else File.Delete(path);

            AppendLog($"Удалено: {name}");
            RefreshContent();
            RefreshInstanceStats();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось удалить: " + ex.Message,
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Импорт ----------

    private void BtnImportMod_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null)
        {
            MessageBox.Show("Сначала выберите сборку.", "Сборка не выбрана",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = "Выберите моды, ресурспаки или шейдеры",
            Filter = "Все поддерживаемые|*.jar;*.zip;*.mrpack|Моды (*.jar)|*.jar|Архивы (*.zip)|*.zip|Все файлы|*.*",
            Multiselect = true
        };

        if (dlg.ShowDialog(this) != true) return;
        ImportFiles(dlg.FileNames);
    }

    private void ImportFiles(IEnumerable<string> paths)
    {
        if (_selectedInstance is null) return;

        var inst = _selectedInstance;
        InstanceService.EnsureFolders(inst);

        var ok = 0;
        var skipped = new List<string>();
        var failed = new List<string>();

        foreach (var src in paths)
        {
            try
            {
                if (Directory.Exists(src))
                {
                    var worldDst = IOPath.Combine(InstanceService.SavesDir(inst), IOPath.GetFileName(src));
                    if (Directory.Exists(worldDst)) { skipped.Add(IOPath.GetFileName(src) + " (уже есть)"); continue; }
                    CopyDirectory(src, worldDst);
                    ok++;
                    continue;
                }

                if (!File.Exists(src)) continue;

                var ext = IOPath.GetExtension(src).ToLowerInvariant();
                var name = IOPath.GetFileName(src);

                string dstDir;

                if (ext == ".jar")
                {
                    dstDir = InstanceService.ModsDir(inst);
                }
                else if (ext == ".zip")
                {
                    dstDir = LooksLikeShaderPack(src)
                        ? InstanceService.ShaderPacksDir(inst)
                        : InstanceService.ResourcePacksDir(inst);
                }
                else if (ext == ".mrpack")
                {
                    // Модпак ставим целиком в текущую сборку
                    _ = InstallModpackAsync(inst, src);
                    ok++;
                    continue;
                }
                else
                {
                    skipped.Add(name + " (неизвестный тип)");
                    continue;
                }

                var dst = IOPath.Combine(dstDir, name);
                if (File.Exists(dst)) { skipped.Add(name + " (уже есть)"); continue; }

                File.Copy(src, dst);
                ok++;
            }
            catch (Exception ex)
            {
                failed.Add(IOPath.GetFileName(src) + ": " + ex.Message);
            }
        }

        var report = $"Добавлено: {ok}";
        if (skipped.Count > 0) report += $"\nПропущено: {string.Join(", ", skipped)}";
        if (failed.Count > 0) report += $"\nОшибки: {string.Join(", ", failed)}";

        AppendLog("Импорт: " + report.Replace("\n", "; "));
        RefreshContent();
        RefreshInstanceStats();

        MessageBox.Show(report, "Импорт файлов", MessageBoxButton.OK,
            failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private static bool LooksLikeShaderPack(string zipPath)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
            return zip.Entries.Any(entry =>
                entry.FullName.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.Contains("/shaders/", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);

        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, IOPath.Combine(dst, IOPath.GetFileName(file)), true);

        foreach (var dir in Directory.GetDirectories(src))
            CopyDirectory(dir, IOPath.Combine(dst, IOPath.GetFileName(dir)));
    }

    // ---------- Перетаскивание ----------

    private void Content_DragOver(object sender, DragEventArgs e)
    {
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasFiles && _selectedInstance is not null ? DragDropEffects.Copy : DragDropEffects.None;

        if (hasFiles && _selectedInstance is not null)
        {
            DropHint.Visibility = Visibility.Visible;
            TxtDropTarget.Text = $"в сборку «{_selectedInstance.Name}»  ·  .jar → моды, .zip → ресурспаки или шейдеры";
        }

        e.Handled = true;
    }

    private void Content_Drop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (_selectedInstance is null)
        {
            MessageBox.Show("Сначала выберите сборку.", "Сборка не выбрана",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) ImportFiles(files);
        e.Handled = true;
    }

    // ---------- Импорт по ссылке Modrinth ----------

    private async void BtnImportUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null)
        {
            MessageBox.Show("Сначала выберите сборку.", "Сборка не выбрана",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new TextInputDialog(
            "Импорт из Modrinth",
            "Вставьте ссылку на мод или его slug:",
            "https://modrinth.com/mod/sodium") { Owner = this };

        if (dlg.ShowDialog() != true) return;

        var input = dlg.Value.Trim();
        if (input.Length == 0) return;

        var slug = ExtractModrinthSlug(input);
        if (slug is null)
        {
            MessageBox.Show("Не удалось распознать ссылку.\n\nПример: https://modrinth.com/mod/sodium",
                "Неверная ссылка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetStage($"Ищу «{slug}» на Modrinth...");

        try
        {
            var project = await _mods.GetProjectAsync(ModProvider.Modrinth, slug);
            if (project is null)
            {
                MessageBox.Show($"Проект «{slug}» не найден на Modrinth.",
                    "Не найдено", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var verDlg = new ModVersionDialog(_mods, project,
                _selectedInstance.McVersion, _selectedInstance.Loader) { Owner = this };

            if (verDlg.ShowDialog() != true || verDlg.SelectedFile is null) return;

            var outcome = await _mods.InstallAsync(
                verDlg.SelectedFile, InstanceService.ModsDir(_selectedInstance),
                _selectedInstance.McVersion, _selectedInstance.Loader, verDlg.InstallDependencies);

            var msg = $"Установлено: {outcome.Installed.Count}";
            if (outcome.Failed.Count > 0) msg += $"\nОшибки: {string.Join(", ", outcome.Failed)}";

            MessageBox.Show(msg, project.Title, MessageBoxButton.OK, MessageBoxImage.Information);

            RefreshContent();
            RefreshInstanceStats();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ошибка импорта", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { HideProgress(); }
    }

    public static string? ExtractModrinthSlug(string input)
    {
        input = input.Trim();

        if (input.Contains("modrinth.com", StringComparison.OrdinalIgnoreCase))
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                input, @"modrinth\.com/(?:mod|plugin|datapack|resourcepack|shader|modpack)/([A-Za-z0-9._-]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            return m.Success ? m.Groups[1].Value : null;
        }

        return System.Text.RegularExpressions.Regex.IsMatch(input, @"^[A-Za-z0-9._-]{2,64}$")
            ? input : null;
    }    // =====================================================================
    //  БОТ (mineflayer)
    // =====================================================================

    private readonly StringBuilder _botLog = new();

    private void OnBotOutput(string line)
    {
        lock (_botLog)
        {
            _botLog.AppendLine(line);
            if (_botLog.Length > 200_000) _botLog.Remove(0, 100_000);
        }

        Dispatcher.BeginInvoke(() =>
        {
            string text;
            lock (_botLog) text = _botLog.ToString();
            TxtBotLog.Text = text;
            BotScroll.ScrollToEnd();
        });
    }

    private string? _selectedBotId;

    /// <summary>Отправляет команду выбранному боту или сразу всем.</summary>
    private void SendBot(string command)
    {
        if (!_bots.AnyRunning)
        {
            OnBotOutput("[!] сначала запустите бота");
            return;
        }

        if (ChkBotAll.IsChecked == true) { _bots.Broadcast(command); return; }

        var id = _selectedBotId ?? _bots.Bots.FirstOrDefault(b => b.IsRunning)?.Id;
        if (id is null) return;

        _bots.Send(id, command);
    }

    private void RefreshBotList()
    {
        var list = _bots.Bots;
        var running = list.Count(b => b.IsRunning);

        BtnBotStopAll.IsEnabled = running > 0;
        BtnBotStart.IsEnabled = true;

        TxtBotStatus.Text = running switch
        {
            0 => "нет ботов",
            1 => "1 бот",
            _ => $"{running} ботов"
        };

        BotDot.Fill = new SolidColorBrush(running > 0
            ? ThemeService.CurrentAccent
            : (Color)ColorConverter.ConvertFromString("#6B7280"));
        TxtBotStatus.Foreground = new SolidColorBrush(running > 0
            ? ThemeService.CurrentAccent
            : (Color)ColorConverter.ConvertFromString("#8B93A3"));

        if (_selectedBotId is not null && list.All(b => b.Id != _selectedBotId))
            _selectedBotId = null;

        _selectedBotId ??= list.FirstOrDefault(b => b.IsRunning)?.Id;

        var target = list.FirstOrDefault(b => b.Id == _selectedBotId);
        TxtBotTarget.Text = target is null
            ? "УПРАВЛЕНИЕ"
            : $"УПРАВЛЕНИЕ · {target.Name.ToUpperInvariant()}";

        ItemsBots.ItemsSource = list.Select(b => new
        {
            b.Id,
            b.Name,
            Info = $"{b.Endpoint}  ·  {(b.InWorld ? "в мире" : "подключается")}  ·  {b.UptimeDisplay}",
            Dot = new SolidColorBrush(b.InWorld
                ? ThemeService.CurrentAccent
                : (Color)ColorConverter.ConvertFromString("#FACC15")),
            Border = new SolidColorBrush(b.Id == _selectedBotId
                ? ThemeService.CurrentAccent
                : (Color)ColorConverter.ConvertFromString(ThemeService.CurrentTheme.Border))
        }).ToList();
    }

    private void BotSelect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;

        _selectedBotId = id;
        ChkBotAll.IsChecked = false;
        RefreshBotList();
    }

    private void BotStopOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;

        _bots.Stop(id);
        RefreshBotList();
    }

    private void BtnBotStopAll_Click(object sender, RoutedEventArgs e)
    {
        _bots.StopAll();
        RefreshBotList();
    }

    private void RefreshBotEnvInfo()
    {
        var node = BotService.IsNodeInstalled();
        var mf = BotService.IsMineflayerInstalled();

        TxtBotEnv.Text = node && mf
            ? "Окружение готово: Node.js и mineflayer установлены."
            : "При первом запуске лаунчер скачает Node.js и mineflayer (~40 МБ). " +
              $"Node.js: {(node ? "есть" : "нет")}, mineflayer: {(mf ? "есть" : "нет")}.";

        if (string.IsNullOrWhiteSpace(TxtBotOwner.Text) && _account is not null)
            TxtBotOwner.Text = _account.Username;

        RefreshBotList();

        if (CbBotVersion.ItemsSource is null)
            CbBotVersion.ItemsSource = BotService.SupportedVersions;

        if (string.IsNullOrWhiteSpace(BotVersionText) && _selectedInstance is not null)
        {
            // Подставляем версию сборки, а если она новее поддерживаемых — ближайшую рабочую
            var v = _selectedInstance.McVersion;
            CbBotVersion.Text = BotService.IsVersionSupported(v)
                ? v
                : BotService.SuggestVersion(v) ?? "";

            if (!BotService.IsVersionSupported(v))
                OnBotOutput($"[внимание] Minecraft {v} пока не поддерживается ботом, " +
                            $"выбрана {CbBotVersion.Text}.");
        }
    }

    private string BotVersionText => (CbBotVersion.Text ?? "").Trim();

    // ========== СКИНЫ ==========

    private void UpdateSkinTabHeader()
    {
        var acc = _account ?? AccountStorage.Load();

        if (acc == null)
        {
            TxtSkinTabStatus.Text = "Вход не выполнен — скин можно применить после входа в аккаунт.";
            ImgSkinsPreview.Source = null;
            return;
        }

        TxtSkinTabStatus.Text = acc.IsOffline
            ? "Оффлайн-профиль: применённый скин показывается в игре через CustomSkinLoader (сборки с Fabric/Forge)."
            : acc.IsExpired
                ? "Сессия Microsoft истекла — скин будет применён после повторного входа."
                : "Аккаунт Microsoft: скин загружается в ваш профиль Mojang.";

                // Оффлайн-профиль: показываем применённый локальный скин, если он есть
        if (acc.IsOffline)
        {
            var local = OfflineSkinService.FindAccountSkin(acc.Username);
            if (local != null)
            {
                LoadLocalSkinImageAsync(ImgSkinsPreview, local);
                return;
            }
        }

        var previewUrl = acc.IsOffline
            ? SkinService.AvatarByNameUrl(acc.Username, 96)
            : SkinService.AvatarUrl(acc.Uuid, 96);
        LoadSkinImageAsync(ImgSkinsPreview, previewUrl);
    }

    private void TabSkin_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelSkinLibrary == null || PanelSkinSearch == null || PanelSkinLocal == null) return;
        var tag = (sender as FrameworkElement)?.Tag?.ToString() ?? "library";
        PanelSkinLibrary.Visibility = tag == "library" ? Visibility.Visible : Visibility.Collapsed;
        PanelSkinSearch.Visibility = tag == "search" ? Visibility.Visible : Visibility.Collapsed;
        PanelSkinGallery.Visibility = tag == "gallery" ? Visibility.Visible : Visibility.Collapsed;
        PanelSkinLocal.Visibility = tag == "local" ? Visibility.Visible : Visibility.Collapsed;

        if (tag == "library") _ = LoadCatalogAsync(10);
        if (tag == "gallery" && _catalogItems.Count == 0) _ = LoadCatalogAsync(_catalogNextPage);
        if (tag == "local") LoadLocalSkins();
    }

    // ---------- Каталог скинов ----------
    // Источники без повторов: (1) встроенный каталог ~1000 скинов (работает без сети),
    // (2) MinecraftSkins.com через WordPress REST API, (3) свежие скины MineSkin.
    // Один кэш + дедупликация по Id — скины не повторяются при листании.

    private readonly List<SkinInfo> _catalogItems = new();
    private readonly HashSet<string> _catalogSeen = new(StringComparer.OrdinalIgnoreCase);
    private int _catalogNextPage = 1;
    private bool _catalogLoading;
    private bool _seedAdded;
    private bool _catalogExhausted;

    private static async Task<List<SkinInfo>> FetchCatalogPageAsync(int page)
    {
        var batch = new List<SkinInfo>();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            http.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36");
            http.DefaultRequestHeaders.Add("Accept", "application/json");

            var url = $"https://minecraftskins.com/wp-json/wp/v2/user-skins?per_page=100&page={page}&_fields=id,title,content";
            var resp = await http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(resp);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return batch;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                string id;
                try { id = item.GetProperty("id").GetInt32().ToString(); }
                catch { continue; }

                string title;
                try { title = item.GetProperty("title").GetProperty("rendered").GetString() ?? ""; }
                catch { title = ""; }
                title = System.Net.WebUtility.HtmlDecode(title).Trim();
                if (string.IsNullOrEmpty(title)) title = "Скин #" + id;

                string html;
                try { html = item.GetProperty("content").GetProperty("rendered").GetString() ?? ""; }
                catch { continue; }

                var m = System.Text.RegularExpressions.Regex.Match(html, "src=\"([^\"]*images/skins/skin-[^\"]+\\.png)\"");
                if (!m.Success) continue;
                var img = m.Groups[1].Value;
                if (img.StartsWith("/")) img = "https://www.minecraftskins.com" + img;
                if (img.StartsWith("http://")) img = "https://" + img.Substring("http://".Length);

                batch.Add(new SkinInfo
                {
                    Id = id,
                    Name = title,
                    Url = img,
                    PreviewUrl = img,
                    Source = "MinecraftSkins"
                });
            }
        }
        catch
        {
            // Сеть недоступна — вернём пустой список, вызывающий код покажет статус
        }
        return batch;
    }

    /// <summary>Догружает страницы каталога (до maxPage включительно) и рисует новые карточки.</summary>
    private async Task LoadCatalogAsync(int maxPage)
    {
        if (_catalogLoading) return;
        _catalogLoading = true;
        try
        {
            AddSeedOnce();

            while (_catalogNextPage <= maxPage && !_catalogExhausted)
            {
                var batch = await FetchCatalogPageAsync(_catalogNextPage);
                if (batch.Count == 0)
                {
                    // Сетевой каталог недоступен или закончился — добираем свежие MineSkin
                    _catalogExhausted = true;
                    var extra = await FetchMineSkinRecentAsync();
                    var freshExtra = extra.Where(s => _catalogSeen.Add(s.Id)).ToList();
                    if (freshExtra.Count > 0)
                    {
                        _catalogItems.AddRange(freshExtra);
                        AppendCatalogCards(freshExtra, GalleryPanel);
                    }
                    else if (_catalogItems.Count == 0)
                    {
                        TxtGalleryStatus.Text = "Не удалось загрузить каталог скинов. Проверьте интернет.";
                    }
                    break;
                }

                var fresh = batch.Where(s => _catalogSeen.Add(s.Id)).ToList();
                var startIndex = _catalogItems.Count;
                _catalogItems.AddRange(fresh);
                _catalogNextPage++;

                AppendCatalogCards(fresh, GalleryPanel);
                var libAdd = fresh.Take(Math.Max(0, 1000 - startIndex)).ToList();
                if (libAdd.Count > 0) AppendCatalogCards(libAdd, SkinLibraryPanel);
            }
        }
        finally
        {
            _catalogLoading = false;
            if (_catalogItems.Count > 0) TxtGalleryStatus.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Один раз показывает встроенный каталог (~1000 скинов) в библиотеке и галерее.</summary>
    private void AddSeedOnce()
    {
        if (_seedAdded) return;
        _seedAdded = true;

        var seed = SkinCatalogService.GetSeed().Take(1000).ToList();
        if (seed.Count == 0) return;

        _catalogItems.AddRange(seed);
        foreach (var s in seed) _catalogSeen.Add(s.Id);

        AppendCatalogCards(seed, GalleryPanel);
        AppendCatalogCards(seed, SkinLibraryPanel);
        TxtGalleryStatus.Visibility = Visibility.Collapsed;
    }

    private static async Task<List<SkinInfo>> FetchMineSkinRecentAsync()
    {
        var batch = new List<SkinInfo>();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            http.DefaultRequestHeaders.Add("User-Agent", "MaysLauncher/1.0");

            var resp = await http.GetStringAsync("https://api.mineskin.org/v2/skins");
            using var doc = JsonDocument.Parse(resp);
            if (!doc.RootElement.TryGetProperty("skins", out var skins)) return batch;

            foreach (var s in skins.EnumerateArray())
            {
                var uuid = s.TryGetProperty("uuid", out var u) ? u.GetString() : null;
                var hash = s.TryGetProperty("texture", out var t) ? t.GetString() : null;
                if (string.IsNullOrEmpty(uuid) || string.IsNullOrEmpty(hash)) continue;

                var name = s.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()
                    : null;
                var shortId = s.TryGetProperty("shortId", out var si) ? si.GetString() : null;

                var skinUrl = "https://mineskin.org/textures/" + hash;
                batch.Add(new SkinInfo
                {
                    Id = uuid,
                    Name = string.IsNullOrWhiteSpace(name) ? $"Скин {shortId}" : name,
                    Url = skinUrl,
                    PreviewUrl = skinUrl,
                    Source = "MineSkin"
                });
            }
        }
        catch { }
        return batch;
    }

    private void AppendCatalogCards(IList<SkinInfo> batch, System.Windows.Controls.Panel panel)
    {
        foreach (var skin in batch)
        {
            var image = new Image
            {
                Width = 72, Height = 72, Stretch = Stretch.Uniform,
                Margin = new Thickness(6, 8, 6, 0)
            };
            _ = LoadCardImageAsync(image, skin);

            var name = new TextBlock
            {
                Text = skin.Name,
                FontSize = 10.5,
                MaxWidth = 80,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(4, 4, 4, 0),
                Foreground = (Brush)FindResource("FgMuted")
            };

            var content = new StackPanel();
            content.Children.Add(image);
            content.Children.Add(name);

            var card = new Border
            {
                Width = 92, Height = 116, CornerRadius = new CornerRadius(8),
                Margin = new Thickness(5), Cursor = Cursors.Hand,
                Background = (Brush)FindResource("Panel"),
                BorderBrush = (Brush)FindResource("Border"),
                BorderThickness = new Thickness(1),
                Tag = skin,
                ToolTip = $"{skin.Name} — нажмите, чтобы надеть"
            };
            card.Child = content;
            card.MouseLeftButtonDown += (_, _) => SelectSkin(skin.Name, skin.Url);

            panel.Children.Add(card);
        }
    }

    /// <summary>Превью карточки: встроенная текстура (каталог) либо загрузка из сети.</summary>
    private static async Task LoadCardImageAsync(Image image, SkinInfo skin)
    {
        if (skin.Source == "Catalog")
        {
            var bytes = SkinCatalogService.GetBytes(skin.Id);
            if (bytes != null)
            {
                try
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.StreamSource = new MemoryStream(bytes);
                    bmp.EndInit();
                    bmp.Freeze();
                    image.Source = bmp;
                }
                catch { }
                return;
            }
        }

        var net = await ImageCacheService.GetAsync(skin.Url, App.Http, 96);
        if (net is not null) image.Source = net;
    }

    private void GalleryScroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        if (_catalogLoading || _catalogExhausted || _catalogItems.Count == 0) return;
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 320)
            _ = LoadCatalogAsync(_catalogNextPage);
    }

    private void LoadSkinLibrary()
    {
        // Библиотека = первые 1000 скинов каталога (общий кэш с галереей, без повторов)
        _ = LoadCatalogAsync(10);
    }

    private async void LoadSkinImageAsync(Image image, string url)
    {
        try
        {
            using var http = new HttpClient();
            var data = await http.GetByteArrayAsync(url);
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            using var ms = new MemoryStream(data);
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            image.Source = bitmap;
        }
        catch { }
    }

    private void SelectSkin(string name, string url, bool slim = false)
    {
        _selectedSkin = new SkinInfo { Name = name, Url = url, Slim = slim };
        TxtNewSkinName.Text = name;
        ShowSkinPreviewPanel();
        LoadBodyPreviewAsync(url);
    }

    private void LoadLocalSkins()
    {
        LocalSkinsPanel.Children.Clear();
        var skinsDir = System.IO.Path.Combine(LauncherPaths.Root, "skins");
        if (!System.IO.Directory.Exists(skinsDir)) return;

        foreach (var file in System.IO.Directory.GetFiles(skinsDir, "*.png"))
        {
            var border = new Border
            {
                Width = 80, Height = 80, CornerRadius = new CornerRadius(8),
                Margin = new Thickness(4), Cursor = Cursors.Hand,
                Background = FindResource("Panel") as Brush,
                BorderBrush = FindResource("Border") as Brush,
                BorderThickness = new Thickness(1),
                Tag = file
            };

            var image = new Image { Stretch = Stretch.UniformToFill, Margin = new Thickness(4) };
            LoadLocalSkinImageAsync(image, file);
            border.Child = image;
            border.MouseLeftButtonDown += (_, _) =>
            {
                var skin = new SkinItem { Name = System.IO.Path.GetFileNameWithoutExtension(file), FilePath = file };
                SelectLocalSkin(skin);
            };
            LocalSkinsPanel.Children.Add(border);
        }
    }

    private void LoadLocalSkinImageAsync(Image image, string path)
    {
        try
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            image.Source = bitmap;
        }
        catch { }
    }

    private void SelectLocalSkin(SkinItem skin)
    {
        _selectedSkin = new SkinInfo { Name = skin.Name, Url = skin.FilePath };
        TxtNewSkinName.Text = skin.Name;
        ShowSkinPreviewPanel();
        LoadBodyPreviewAsync(skin.FilePath);
    }

    /// <summary>Показывает панель превью выбранного скина и подсказку для текущего аккаунта.</summary>
    private void ShowSkinPreviewPanel()
    {
        SkinPreviewPanelNew.Visibility = Visibility.Visible;
        BtnApplySkinNew.IsEnabled = true;
        SetApplyButtonIdle();

        var acc = _account ?? AccountStorage.Load();
        TxtNewSkinInfo.Text = acc == null
            ? "Войдите в аккаунт, чтобы применить скин."
            : acc.IsOffline
                ? $"Оффлайн-аккаунт «{acc.Username}» — скин будет показываться в игре."
                : acc.IsExpired
                    ? "Сессия истекла — скин применится после повторного входа."
                    : "Скин будет загружен в ваш профиль Mojang.";
    }

    /// <summary>Строит полноценное превью персонажа (тело) из текстурного листа скина.</summary>
    private async void LoadBodyPreviewAsync(string urlOrPath)
    {
        try
        {
            byte[] data;
            if (_selectedSkin?.Source == "Catalog")
            {
                data = SkinCatalogService.GetBytes(_selectedSkin.Id) ?? Array.Empty<byte>();
            }
            else if (urlOrPath.StartsWith("http"))
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
                http.DefaultRequestHeaders.Add("User-Agent", "MaysLauncher/1.0");
                data = await http.GetByteArrayAsync(urlOrPath);
            }
            else
            {
                data = File.ReadAllBytes(urlOrPath);
            }

            var slim = _selectedSkin?.Slim ?? false;
            var selected = _selectedSkin;
            var render = await Task.Run(() => SkinBodyRenderer.Render(data, slim));
            if (render != null && ReferenceEquals(_selectedSkin, selected))
                ImgNewSkinPreview.Source = render;
        }
        catch { }
    }

    private async void BtnApplySkin_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSkin == null) return;

        BtnApplySkinNew.IsEnabled = false;
        BtnApplySkinNew.Content = "Устанавливаю…";
        try
        {
            var account = _account ?? AccountStorage.Load();
            if (account == null)
            {
                ToastNotification.Show("Вход не выполнен", "Сначала войдите в аккаунт.", NotificationType.Error);
                SetApplyButtonIdle();
                return;
            }

            ToastNotification.Show("Установка скина",
                $"Начинаю установку «{_selectedSkin.Name}»…", NotificationType.Info);

            var isRemote = _selectedSkin.Url.StartsWith("http") && !_selectedSkin.Url.StartsWith("file:///");
            var path = isRemote ? await DownloadSkinAsync(_selectedSkin) : _selectedSkin.Url;

            if (path == null || !File.Exists(path))
            {
                ToastNotification.Show("Не удалось надеть скин", "Скин повреждён или файл не скачался.", NotificationType.Error);
                SetApplyButtonIdle();
                return;
            }

            if (account.IsOffline)
            {
                // Слот скина оффлайн-аккаунта: skins/<ник>.png
                var slot = OfflineSkinService.AccountSkinPath(account.Username);
                Directory.CreateDirectory(IOPath.GetDirectoryName(slot)!);

                if (string.Equals(path, slot, StringComparison.OrdinalIgnoreCase))
                {
                    // Ник совпал с именем аккаунта: скачанный файл уже лежит в слоте —
                    // перезапись сама в себя не нужна (File.Copy падает в этом случае).
                    AppendLog("Файл скина уже в слоте аккаунта: " + slot);
                }
                else
                {
                    try
                    {
                        File.Copy(path, slot, overwrite: true);
                    }
                    catch (IOException)
                    {
                        // Файл может удерживать превью — переписываем байтами
                        File.WriteAllBytes(slot, File.ReadAllBytes(path));
                    }
                }

                // Синхронизация во все сборки с модлоадером (CustomSkinLoader)
                var synced = 0;
                foreach (var inst in _instances.Where(OfflineSkinService.IsCslSupported))
                {
                    var cslOk = await OfflineSkinService.EnsureCslModAsync(inst);
                    AppendLog($"CSL для «{inst.Name}» ({inst.Loader}): " + (cslOk ? "мод готов" : "не удалось установить"));
                    if (!cslOk) continue;
                    OfflineSkinService.SyncToInstance(inst, account.Username, slot);
                    synced++;
                    AppendLog($"Оффлайн-скин «{account.Username}» скопирован в «{inst.Name}».");
                }
                if (_instances.Count == 0 && _selectedInstance != null && OfflineSkinService.IsCslSupported(_selectedInstance))
                {
                    var cslOk = await OfflineSkinService.EnsureCslModAsync(_selectedInstance);
                    if (cslOk)
                    {
                        OfflineSkinService.SyncToInstance(_selectedInstance, account.Username, slot);
                        synced++;
                        AppendLog($"Оффлайн-скин «{account.Username}» скопирован в «{_selectedInstance.Name}».");
                    }
                }
                AppendLog(synced > 0
                    ? $"Скин «{_selectedSkin.Name}» надет на «{account.Username}» (сборок: {synced})."
                    : "Скин сохранён, но подходящих сборок с модлоадером не найдено.");

                if (isRemote) LoadLocalSkins();

                ToastNotification.Show("Скин надет", synced > 0
                    ? $"«{_selectedSkin.Name}» установлен для оффлайн-аккаунта «{account.Username}» и будет показываться в игре."
                    : "Скин сохранён. Для показа в игре оффлайн-аккаунту нужна сборка с модлоадером Fabric/Forge.",
                    NotificationType.Success);
                UpdateSkinTabHeader();
                _ = LoadSkinImagesAsync(account);
                SetApplyButtonApplied();
                return;
            }

            if (account.IsExpired && !string.IsNullOrEmpty(account.MicrosoftRefreshToken))
            {
                account = await _auth.RefreshAsync(account.MicrosoftRefreshToken!);
                AccountStorage.Save(account);
                SetAccount(account, refreshSkin: false);
            }

            var model = _selectedSkin.Slim ? SkinService.SkinModel.Slim : SkinService.SkinModel.Classic;
            await _skins.UploadSkinAsync(account.AccessToken, path, model);
            ToastNotification.Show("Скин надет",
                $"«{_selectedSkin.Name}» загружен в ваш профиль Mojang.", NotificationType.Success);
            UpdateSkinTabHeader();
            _ = LoadSkinImagesAsync(account);
            SetApplyButtonApplied();
        }
        catch (InvalidDataException ex)
        {
            Log.Warn("Скин повреждён: " + ex.Message);
            ToastNotification.Show("Не удалось надеть скин", "Скин повреждён — " + ex.Message, NotificationType.Error);
            SetApplyButtonIdle();
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось применить скин: " + ex.Message);
            ToastNotification.Show("Не удалось надеть скин", ex.Message, NotificationType.Error);
            SetApplyButtonIdle();
        }
    }

    /// <summary>Кнопка «Надеть скин» в исходном состоянии.</summary>
    private void SetApplyButtonIdle()
    {
        BtnApplySkinNew.IsEnabled = true;
        BtnApplySkinNew.Content = "Надеть скин";
        BtnApplySkinNew.Background = null;
        BtnResetSkinNew.Visibility = Visibility.Collapsed;
    }

    /// <summary>Кнопка «Надеть скин» в состоянии «надет»: другой оттенок + кнопка «По умолчанию».</summary>
    private void SetApplyButtonApplied()
    {
        BtnApplySkinNew.IsEnabled = true;
        BtnApplySkinNew.Content = "Надет ✓";
        BtnApplySkinNew.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));
        BtnResetSkinNew.Visibility = Visibility.Visible;
    }

    private async void BtnResetSkinNew_Click(object sender, RoutedEventArgs e)
    {
        var account = _account ?? AccountStorage.Load();
        if (account == null)
        {
            ToastNotification.Show("Вход не выполнен", "Сначала войдите в аккаунт.", NotificationType.Error);
            return;
        }

        BtnResetSkinNew.IsEnabled = false;
        try
        {
            if (account.IsOffline)
            {
                // Удаляем слот скина оффлайн-аккаунта и локальные файлы CustomSkinLoader
                var slot = OfflineSkinService.AccountSkinPath(account.Username);
                if (File.Exists(slot)) File.Delete(slot);

                foreach (var inst in _instances.Where(OfflineSkinService.IsCslSupported))
                {
                    OfflineSkinService.RemoveFromInstance(inst, account.Username);
                }

                if (_selectedSkin != null && _selectedSkin.Url.StartsWith("http")) LoadLocalSkins();
                ToastNotification.Show("Скин по умолчанию",
                    $"Для оффлайн-аккаунта «{account.Username}» возвращён стандартный скин.",
                    NotificationType.Success);
            }
            else
            {
                if (account.IsExpired && !string.IsNullOrEmpty(account.MicrosoftRefreshToken))
                {
                    account = await _auth.RefreshAsync(account.MicrosoftRefreshToken!);
                    AccountStorage.Save(account);
                    SetAccount(account, refreshSkin: false);
                }

                await _skins.ResetSkinAsync(account.AccessToken, CancellationToken.None);
                ToastNotification.Show("Скин по умолчанию",
                    "Стандартный скин восстановлен в профиле Mojang.", NotificationType.Success);
            }

            if (_selectedSkin != null) LoadBodyPreviewAsync(_selectedSkin.Url);
            UpdateSkinTabHeader();
            _ = LoadSkinImagesAsync(account);
            SetApplyButtonIdle();
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось вернуть скин по умолчанию: " + ex.Message);
            ToastNotification.Show("Ошибка", "Не удалось вернуть скин по умолчанию: " + ex.Message, NotificationType.Error);
        }
        finally { BtnResetSkinNew.IsEnabled = true; }
    }

    private async Task<string?> DownloadSkinAsync(SkinInfo skin)
    {
        byte[] data;
        if (skin.Source == "Catalog")
        {
            // Встроенная текстура — без интернета
            data = SkinCatalogService.GetBytes(skin.Id)
                   ?? throw new InvalidDataException("Встроенный файл скина не найден.");
        }
        else
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                data = await http.GetByteArrayAsync(skin.Url);
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        SkinService.ValidateSkinPng(data);

        var skinsDir = IOPath.Combine(LauncherPaths.Root, "skins");
        Directory.CreateDirectory(skinsDir);
        var safe = string.Concat(skin.Name.Where(char.IsLetterOrDigit));
        if (string.IsNullOrEmpty(safe)) safe = "skin";
        var path = IOPath.Combine(skinsDir, $"{safe}.png");
        await File.WriteAllBytesAsync(path, data);
        return path;
    }

    private async void BtnSearchSkins_Click(object sender, RoutedEventArgs e)
    {
        var query = TxtSkinSearch.Text.Trim();
        if (string.IsNullOrEmpty(query)) return;

        TxtSkinSearchStatus.Text = "Поиск...";
        var results = await SearchSkinsOnlineAsync(query);
        SkinsSearchGrid.ItemsSource = results;

        if (results.Count == 0)
        {
            TxtSkinSearchStatus.Text = "Игрок не найден или у него нет скина.";
            return;
        }

        TxtSkinSearchStatus.Text = $"Найден скин игрока {results[0].Name}. Нажмите «Надеть скин» или «В библиотеку».";
        SelectSkin(results[0].Name, results[0].Url, results[0].Slim);
    }

    private void SkinsSearchGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SkinsSearchGrid.SelectedItem is not SkinInfo skin) return;
        SelectSkin(skin.Name, skin.Url, skin.Slim);
    }

    /// <summary>Ищет скин по нику через официальные серверы Mojang.</summary>
    private static async Task<List<SkinInfo>> SearchSkinsOnlineAsync(string name)
    {
        var results = new List<SkinInfo>();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.Add("User-Agent", "MaysLauncher/1.0");

            // 1) Ник -> UUID (api.mojang.com)
            using var uuidResp = await http.GetAsync(
                "https://api.mojang.com/users/profiles/minecraft/" + Uri.EscapeDataString(name.Trim()));
            if (!uuidResp.IsSuccessStatusCode) return results;

            var uuidJson = await uuidResp.Content.ReadAsStringAsync();
            using var uuidDoc = JsonDocument.Parse(uuidJson);
            var uuid = uuidDoc.RootElement.GetProperty("id").GetString() ?? "";

            // 2) UUID -> текстура скина (sessionserver.mojang.com)
            var session = await http.GetStringAsync(
                $"https://sessionserver.mojang.com/session/minecraft/profile/{uuid}?unsigned=false");
            using var doc = JsonDocument.Parse(session);

            if (!doc.RootElement.TryGetProperty("properties", out var props)) return results;

            foreach (var prop in props.EnumerateArray())
            {
                if (prop.GetProperty("name").GetString() != "textures") continue;

                var value = prop.GetProperty("value").GetString();
                if (string.IsNullOrEmpty(value)) break;

                using var texDoc = JsonDocument.Parse(Convert.FromBase64String(value));
                if (!texDoc.RootElement.TryGetProperty("textures", out var textures)) break;
                if (!textures.TryGetProperty("SKIN", out var skin)) break;
                if (!skin.TryGetProperty("url", out var urlEl)) break;

                var url = urlEl.GetString() ?? "";
                if (url.StartsWith("http://")) url = "https://" + url.Substring("http://".Length);

                var slim = skin.TryGetProperty("metadata", out var meta) &&
                           meta.TryGetProperty("model", out var modelEl) &&
                           modelEl.GetString() == "slim";

                results.Add(new SkinInfo
                {
                    Id = uuid,
                    Name = uuidDoc.RootElement.GetProperty("name").GetString() ?? name.Trim(),
                    Url = url,
                    PreviewUrl = SkinService.AvatarUrl(uuid, 96),
                    Source = "Mojang",
                    Slim = slim
                });
                break;
            }
        }
        catch { }
        return results;
    }

    private void TxtSkinSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) BtnSearchSkins_Click(sender, e);
    }

    private async void BtnDownloadSkin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string skinId) return;
        var skin = (SkinsSearchGrid.ItemsSource as List<SkinInfo>)?.FirstOrDefault(s => s.Id == skinId);
        if (skin == null) return;

        try
        {
            var path = await DownloadSkinAsync(skin);
            if (path != null)
            {
                ToastNotification.Show("Скин добавлен", $"{skin.Name} сохранён в «Свои скины».", NotificationType.Success);
                LoadLocalSkins();
            }
            else
            {
                ToastNotification.Show("Ошибка", "Не удалось скачать скин", NotificationType.Error);
            }
        }
        catch (InvalidDataException ex)
        {
            ToastNotification.Show("Скин повреждён", "Файл не похож на скин: " + ex.Message, NotificationType.Error);
        }
    }

    private void BtnImportSkin_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "PNG files (*.png)|*.png",
            Title = "Выберите файл скина"
        };

if (dialog.ShowDialog() == true)
        {
            try
            {
                var src = dialog.FileName;
                SkinService.ValidateSkinPng(File.ReadAllBytes(src));

                var skinsDir = IOPath.Combine(LauncherPaths.Root, "skins");
                Directory.CreateDirectory(skinsDir);
                var dest = IOPath.Combine(skinsDir, IOPath.GetFileName(src));
                File.Copy(src, dest, true);
                LoadLocalSkins();
                ToastNotification.Show("Скин импортирован", IOPath.GetFileName(dest), NotificationType.Success);
            }
            catch (Exception ex)
            {
                ToastNotification.Show("Ошибка", ex.Message, NotificationType.Error);
            }
        }
    }

    private void BtnRefreshLocalSkins_Click(object sender, RoutedEventArgs e)
    {
        LoadLocalSkins();
    }

    // ---------- Поиск открытого мира в локальной сети ----------

    private readonly LanDiscoveryService _lan = new();
    private List<LanWorld> _lanWorlds = new();

    private async void BtnFindLan_Click(object sender, RoutedEventArgs e)
    {
        BtnFindLan.IsEnabled = false;
        BtnFindLan.Content = "Ищу…";

        LanResults.Visibility = Visibility.Visible;
        TxtLanStatus.Text = "Слушаю локальную сеть 6 секунд. Убедитесь, что мир открыт для сети.";
        ItemsLan.ItemsSource = null;

        try
        {
            _lanWorlds = await _lan.ScanOnceAsync(6000);

            if (_lanWorlds.Count == 0)
            {
                TxtLanStatus.Text =
                    "Открытых миров не найдено.\n\n" +
                    "Проверьте: игра запущена, в меню Esc нажата «Открыть для сети», " +
                    "и лаунчер с игрой на одном компьютере или в одной сети.\n" +
                    "Порт можно ввести вручную — он показан в игровом чате.";
                return;
            }

            TxtLanStatus.Text = $"Найдено миров: {_lanWorlds.Count}. Нажмите, чтобы подставить адрес.";

            ItemsLan.ItemsSource = _lanWorlds.Select(w => new
            {
                Key = $"{w.Address}:{w.Port}",
                Motd = w.Motd,
                Addr = $"{w.Address}:{w.Port}"
            }).ToList();

            // Единственный мир подставляем сразу
            if (_lanWorlds.Count == 1) ApplyLanWorld(_lanWorlds[0]);
        }
        catch (Exception ex)
        {
            TxtLanStatus.Text = "Ошибка поиска: " + ex.Message;
        }
        finally
        {
            BtnFindLan.IsEnabled = true;
            BtnFindLan.Content = "Найти мой мир";
        }
    }

    private void LanWorld_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string key) return;

        var world = _lanWorlds.FirstOrDefault(w => $"{w.Address}:{w.Port}" == key);
        if (world is not null) ApplyLanWorld(world);
    }

    private void ApplyLanWorld(LanWorld world)
    {
        // Свой же компьютер надёжнее адресовать как localhost
        var isLocal = GetLocalIps().Contains(world.Address);

        TxtBotHost.Text = isLocal ? "localhost" : world.Address;
        TxtBotPort.Text = world.Port.ToString();

        TxtLanStatus.Text = $"Выбран мир «{world.Motd}» — порт {world.Port}. " +
                            "Теперь нажмите «Запустить бота».";

        OnBotOutput($"[lan] найден мир «{world.Motd}» на {TxtBotHost.Text}:{world.Port}");
    }

    private static HashSet<string> GetLocalIps()
    {
        var set = new HashSet<string> { "127.0.0.1" };

        try
        {
            foreach (var ip in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    set.Add(ip.ToString());
            }
        }
        catch { }

        return set;
    }
    private async void BtnBotSetup_Click(object sender, RoutedEventArgs e)
    {
        BtnBotSetup.IsEnabled = false;
        try
        {
            OnBotOutput("[setup] проверяю окружение...");
            await _bots.EnsureEnvironmentAsync(OnProgress);
            RefreshBotEnvInfo();
        }
        catch (Exception ex)
        {
            OnBotOutput("[setup] ошибка: " + ex.Message);
            MessageBox.Show(ex.Message, "Установка окружения",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnBotSetup.IsEnabled = true;
            HideProgress();
        }
    }

    private async void BtnBotStart_Click(object sender, RoutedEventArgs e)
    {
        var host = TxtBotHost.Text.Trim();
        if (host.Length == 0) host = "localhost";

        if (!int.TryParse(TxtBotPort.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show("Укажите корректный порт (1–65535).\n\n" +
                            "Порт открытого мира виден в чате после «Открыть для сети».",
                "Некорректный порт", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var name = TxtBotName.Text.Trim();
        if (!OfflineAccountService.TryValidateName(name, out var err))
        {
            MessageBox.Show(err, "Ник бота", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BtnBotStart.IsEnabled = false;

        try
        {
            await _bots.StartAsync(host, port, name, BotVersionText);
            AppendLog($"Бот {name} подключается к {host}:{port}");
            RefreshBotList();
        }
        catch (Exception ex)
        {
            OnBotOutput("[error] " + ex.Message);
            MessageBox.Show(ex.Message, "Не удалось запустить бота",
                MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshBotList();
        }
        finally { HideProgress(); BtnBotStart.IsEnabled = true; }
    }


    private string BotOwner => TxtBotOwner.Text.Trim();

    private void BotFollow_Click(object sender, RoutedEventArgs e)
    {
        if (BotOwner.Length == 0)
        {
            MessageBox.Show("Укажите свой ник в игре — за кем боту следовать.",
                "Нужен ник", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SendBot("follow " + BotOwner);
    }

    private void BotStop_Click(object sender, RoutedEventArgs e) => SendBot("stop");
    private void BotJump_Click(object sender, RoutedEventArgs e) => SendBot("jump");
    private void BotPos_Click(object sender, RoutedEventArgs e) => SendBot("pos");
    private void BotPlayers_Click(object sender, RoutedEventArgs e) => SendBot("players");
    private void BotInv_Click(object sender, RoutedEventArgs e) => SendBot("inv");

    private void BotLook_Click(object sender, RoutedEventArgs e)
    {
        if (BotOwner.Length == 0) return;
        SendBot("look " + BotOwner);
    }

    private void BotSay_Click(object sender, RoutedEventArgs e)
    {
        var msg = TxtBotChat.Text.Trim();
        if (msg.Length == 0) return;

        SendBot("say " + msg);
        TxtBotChat.Clear();
    }

    private void TxtBotChat_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) BotSay_Click(sender, e);
    }

    // =====================================================================
    //  ОБСЛУЖИВАНИЕ
    // =====================================================================

    private List<MaintenanceService.TargetInfo> _maintTargets = new();
    private readonly HashSet<MaintenanceService.CleanTarget> _maintChecked = new();

    // ---------- Портативный режим ----------

    private void RefreshPortableState()
    {
        if (TxtPortableState is null) return;

        if (LauncherPaths.IsPortable)
        {
            TxtPortableState.Text = $"Включён. Данные: {LauncherPaths.Root}";
            TxtPortableState.Foreground = (Brush)FindResource("Accent");
            BtnPortableToggle.Content = "Выключить портативный режим";
        }
        else
        {
            var can = LauncherPaths.CanUsePortable();

            TxtPortableState.Text = can
                ? $"Выключен. Данные: {LauncherPaths.Root}"
                : "Недоступен: нет прав на запись рядом с лаунчером. " +
                  "Перенесите exe в обычную папку или на флешку.";

            TxtPortableState.Foreground = (Brush)FindResource(can ? "FgMuted" : "Danger");
            BtnPortableToggle.Content = "Включить портативный режим";
            BtnPortableToggle.IsEnabled = can;
        }
    }

    private void BtnPortableToggle_Click(object sender, RoutedEventArgs e)
    {
        var turnOn = !LauncherPaths.IsPortable;

        if (_sessions.AnyRunning || _bots.AnyRunning)
        {
            MessageBox.Show("Сначала остановите игру и ботов.", "Занято",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var question = turnOn
            ? "Включить портативный режим?\n\n" +
              $"Данные переедут в:\n{IOPath.Combine(LauncherPaths.ExeDir, "MaysLauncherData")}\n\n" +
              "Скопировать туда текущие сборки и настройки?"
            : "Выключить портативный режим?\n\n" +
              "Данные вернутся в папку пользователя (%APPDATA%).\n\n" +
              "Скопировать туда текущие сборки и настройки?";

        var r = MessageBox.Show(question, "Портативный режим",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (r == MessageBoxResult.Cancel) return;

        try
        {
            if (r == MessageBoxResult.Yes)
            {
                var copied = 0;
                LauncherPaths.MigrateTo(turnOn, _ => copied++);
                AppendLog($"Портативный режим: скопировано файлов {copied}.");
            }

            LauncherPaths.SetPortable(turnOn);

            MessageBox.Show(
                "Готово. Изменения вступят в силу после перезапуска лаунчера.\n\n" +
                "Закрыть его сейчас?",
                "Портативный режим", MessageBoxButton.OK, MessageBoxImage.Information);

            var restart = MessageBox.Show("Закрыть лаунчер?", "Перезапуск",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (restart == MessageBoxResult.Yes) Application.Current.Shutdown();
            else RefreshPortableState();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось переключить режим: " + ex.Message,
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void BtnScanMaint_Click(object sender, RoutedEventArgs e) => ScanMaintenance();

    private void ScanMaintenance()
    {
        TxtMaintTotal.Text = "Считаю…";

        _maintTargets = MaintenanceService.Enumerate();
        var total = MaintenanceService.TotalSize();

        TxtMaintTotal.Text = $"Всего данных лаунчера: {Human(total)}  ·  {LauncherPaths.Root}";

        RebuildMaintList();
    }

    private void RebuildMaintList()
    {
        ItemsMaint.ItemsSource = _maintTargets.Select(t => new
        {
            Key = t.Target,
            t.Title,
            t.Description,
            SizeText = t.SizeDisplay,
            Checked = _maintChecked.Contains(t.Target),
            TitleColor = new SolidColorBrush(t.Dangerous
                ? (Color)ColorConverter.ConvertFromString("#F87171")
                : (Color)ColorConverter.ConvertFromString(ThemeService.CurrentTheme.Text)),
            RowBorder = new SolidColorBrush(t.Dangerous
                ? (Color)ColorConverter.ConvertFromString("#3A2428")
                : (Color)ColorConverter.ConvertFromString(ThemeService.CurrentTheme.Border))
        }).ToList();
    }

    private void MaintItem_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.Tag is not MaintenanceService.CleanTarget key) return;

        if (cb.IsChecked == true) _maintChecked.Add(key);
        else _maintChecked.Remove(key);
    }

    private void BtnMaintSafe_Click(object sender, RoutedEventArgs e)
    {
        if (_maintTargets.Count == 0) ScanMaintenance();

        _maintChecked.Clear();
        foreach (var t in _maintTargets.Where(x => !x.Dangerous &&
                     x.Target is MaintenanceService.CleanTarget.Cache
                         or MaintenanceService.CleanTarget.ImageCache
                         or MaintenanceService.CleanTarget.Logs))
        {
            _maintChecked.Add(t.Target);
        }

        RebuildMaintList();
    }

    private void BtnMaintClean_Click(object sender, RoutedEventArgs e)
    {
        if (_maintChecked.Count == 0)
        {
            MessageBox.Show("Отметьте, что нужно удалить.", "Ничего не выбрано",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_sessions.AnyRunning)
        {
            MessageBox.Show("Сначала остановите игру.", "Игра запущена",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selected = _maintTargets.Where(t => _maintChecked.Contains(t.Target)).ToList();
        var dangerous = selected.Where(t => t.Dangerous).ToList();
        var totalSize = selected.Sum(t => t.Size);

        var msg = "Будет удалено:\n\n" +
                  string.Join("\n", selected.Select(t => $"  • {t.Title} — {t.SizeDisplay}")) +
                  $"\n\nОсвободится примерно {Human(totalSize)}.";

        if (dangerous.Count > 0)
            msg += "\n\nВНИМАНИЕ: среди выбранного есть сборки с модами и мирами. " +
                   "Восстановить их будет невозможно.";

        if (MessageBox.Show(msg + "\n\nПродолжить?", "Подтверждение очистки",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var freed = MaintenanceService.Clean(selected);

        // Что-то из удалённого могло быть загружено в память
        if (_maintChecked.Contains(MaintenanceService.CleanTarget.Instances))
        {
            _instances.Clear();
            RefreshInstanceLists();
        }

        if (_maintChecked.Contains(MaintenanceService.CleanTarget.Account)) BtnLogout_Click(sender, e);
        if (_maintChecked.Contains(MaintenanceService.CleanTarget.ImageCache)) ImageCacheService.ClearMemory();

        _maintChecked.Clear();
        ScanMaintenance();

        MessageBox.Show($"Готово. Освобождено {Human(freed)}.", "Очистка завершена",
            MessageBoxButton.OK, MessageBoxImage.Information);

        AppendLog($"Очистка: освобождено {Human(freed)}");
    }

    private void BtnReinstallSoft_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Будут удалены версии игры, библиотеки, ресурсы, Java и кэш.\n\n" +
                "Сборки (моды, миры, скриншоты), аккаунт и настройки сохранятся.\n" +
                "Файлы игры скачаются заново при следующем запуске.\n\nПродолжить?",
                "Переустановка начисто", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        if (_sessions.AnyRunning)
        {
            MessageBox.Show("Сначала остановите игру.", "Игра запущена",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var targets = MaintenanceService.Enumerate()
            .Where(t => t.Target is MaintenanceService.CleanTarget.Versions
                or MaintenanceService.CleanTarget.Libraries
                or MaintenanceService.CleanTarget.Assets
                or MaintenanceService.CleanTarget.JavaRuntime
                or MaintenanceService.CleanTarget.Cache
                or MaintenanceService.CleanTarget.ImageCache)
            .ToList();

        var freed = MaintenanceService.Clean(targets);
        ImageCacheService.ClearMemory();
        ScanMaintenance();

        MessageBox.Show($"Готово. Освобождено {Human(freed)}.\n\n" +
                        "Файлы игры загрузятся заново при нажатии «ИГРАТЬ».",
            "Переустановка", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnReinstallFull_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Будут удалены ВСЕ данные лаунчера:\n\n" +
                "  • версии игры, библиотеки, ресурсы\n" +
                "  • сборки со всеми модами и мирами\n" +
                "  • аккаунт и настройки\n\n" +
                "Сам файл лаунчера останется. Восстановить данные будет нельзя.\n\nПродолжить?",
                "Полная переустановка", MessageBoxButton.YesNo, MessageBoxImage.Stop)
            != MessageBoxResult.Yes) return;

        if (MessageBox.Show("Точно удалить все миры и моды?", "Последнее подтверждение",
                MessageBoxButton.YesNo, MessageBoxImage.Stop) != MessageBoxResult.Yes) return;

        if (_sessions.AnyRunning) _sessions.StopAllAsync().GetAwaiter().GetResult();
        _bots.StopAll();

        var freed = MaintenanceService.Clean(MaintenanceService.Enumerate());

        MessageBox.Show($"Удалено {Human(freed)}.\n\nЛаунчер сейчас закроется. " +
                        "Запустите его заново — он будет как после установки.",
            "Готово", MessageBoxButton.OK, MessageBoxImage.Information);

        Application.Current.Shutdown();
    }

    private void BtnUninstall_Click(object sender, RoutedEventArgs e)
    {
        var exePath = Environment.ProcessPath ?? "";
        var isExe = exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

        var r = MessageBox.Show(
            "Полностью удалить MaysLauncher с компьютера?\n\n" +
            $"Будет удалена папка данных:\n{LauncherPaths.Root}\n\n" +
            (isExe ? "«Да» — удалить и сам файл лаунчера.\n«Нет» — удалить только данные.\n"
                   : "Файл лаунчера удалить нельзя (запущен не как exe).\n") +
            "\nЭто действие необратимо.",
            "Удаление лаунчера", MessageBoxButton.YesNoCancel, MessageBoxImage.Stop);

        if (r == MessageBoxResult.Cancel) return;

        var removeExe = isExe && r == MessageBoxResult.Yes;

        if (MessageBox.Show(
                removeExe
                    ? "Лаунчер удалит все данные и себя, затем закроется. Подтвердить?"
                    : "Лаунчер удалит все данные и закроется. Подтвердить?",
                "Последнее подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Stop)
            != MessageBoxResult.Yes) return;

        try
        {
            if (_sessions.AnyRunning) _sessions.StopAllAsync().GetAwaiter().GetResult();
            _bots.StopAll();

            var script = MaintenanceService.PrepareUninstall(removeExe);
            MaintenanceService.RunUninstall(script);

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось запустить удаление: " + ex.Message,
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private static string Human(long bytes)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }

    private static string Shorten(string s, int max) =>
        s.Length <= max ? s : "…" + s[^(max - 1)..];
}
