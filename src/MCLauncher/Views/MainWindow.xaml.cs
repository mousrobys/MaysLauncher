using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MCLauncher.Models;
using MCLauncher.Services;
using Microsoft.Win32;

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
    private readonly TwitchAuthService _twitchAuth;
    private readonly TwitchStreamService _twitchStream;

    private TwitchAccount? _twitchAccount;
    private TwitchStreamInfo? _currentStreamInfo;
    private DispatcherTimer? _streamCheckTimer;

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
        _twitchAuth = new TwitchAuthService();
        _twitchStream = new TwitchStreamService(http);
        _twitchStream.StreamStatusChanged += OnTwitchStreamChanged;

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
    //  ������ ����������
    // =====================================================================

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        _initializing = true;
        _settings = SettingsService.Load();

        // ��������������� ���� �����, ���� ��� ����
        if (!string.IsNullOrWhiteSpace(_settings.CustomThemeJson))
        {
            try
            {
                ThemeService.CustomPreset =
                    System.Text.Json.JsonSerializer.Deserialize<ThemePreset>(_settings.CustomThemeJson);
            }
            catch (Exception ex) { Log.Warn("���� ���� ����������: " + ex.Message); }
        }

        ThemeService.ApplyTheme(_settings.Theme);
        ThemeService.ApplyAccent(_settings.AccentColor);
        ApplySettingsToUi();
        BuildThemeCards();
        BuildAccentSwatches();
        BuildBackgroundStyleButtons();
        ApplyBanner();
        ApplyWindowBackground();

        AppendLog("MaysLauncher �������. �����: " + _settings.GameDir);

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
                    SetStage("�������� ������ Microsoft...");
                    var refreshed = await _auth.RefreshAsync(saved.MicrosoftRefreshToken!);
                    AccountStorage.Save(refreshed);
                    SetAccount(refreshed, refreshSkin: true);
                }
catch (Exception ex)
                {
                    AppendLog("Не удалось обновить сессию: " + ex.Message);
                }
                finally { HideProgress(); }
            }
        }

        await LoadVersionsAsync();
        LoadInstances();
        _initializing = false;
        UpdateRunStateUi();

        // ������ ���� �� ������ ������ �������� � ������� ��� ��������� ��������
        Dispatcher.BeginInvoke(new Action(() => SetupWheelHandling(this)),
            System.Windows.Threading.DispatcherPriority.Loaded);

        _ = RefreshServersAsync();
        InitializeTwitch();
    }

    private void InitializeTwitch()
    {
        if (_settings.HideStreams)
        {
            NavStreams.Visibility = Visibility.Collapsed;
            return;
        }

        _twitchAccount = TwitchStorage.Load();
        if (_twitchAccount != null)
        {
            _twitchStream.StartMonitoring(_twitchAccount);
            UpdateTwitchUI();
        }
    }

    private void OnTwitchStreamChanged(TwitchStreamInfo? info)
    {
        Dispatcher.Invoke(() =>
        {
            _currentStreamInfo = info;
            UpdateStreamInfoDisplay(info);

            if (info?.IsLive == true)
            {
                ShowStreamNotification(info);
            }
        });
    }

    private void ShowStreamNotification(TwitchStreamInfo info)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell",
                $"-Command \"[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null; " +
                "$template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastToastTemplateType]::ToastText02); " +
                "$text = $template.GetElementsByTagName('text'); " +
                "$text[0].AppendChild($template.CreateTextNode('moysecamm_tw �������� �����!')) | Out-Null; " +
                "$text[1].AppendChild($template.CreateTextNode('{info.Title}')) | Out-Null; " +
                "$toast = [Windows.UI.Notifications.ToastNotification]::new($template); " +
                "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('MaysLauncher').Show($toast)\"")
            {
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Warn("Toast notification failed: " + ex.Message);
        }
    }


    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_sessions.AnyRunning)
        {
            var r = MessageBox.Show(
                $"������ �������� ���: {_sessions.RunningCount}.\n\n" +
                "������� ������� ������ � �����?\n" +
                "���� � ������� ���������, ���� ��������� ��������.",
                "���� ��������", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (r == MessageBoxResult.Yes) _sessions.StopAllAsync().GetAwaiter().GetResult();
        }

        _uptimeTimer?.Stop();
        PersistSettings();
    }

    // =====================================================================
    //  ��������� <-> UI
    // =====================================================================

    private void ApplySettingsToUi()
    {
        if (string.IsNullOrWhiteSpace(_settings.GameDir)) _settings.GameDir = LauncherPaths.Root;

        SldMemory.Value = Math.Clamp(_settings.MaxMemoryMb, 1024, 16384);
        TxtMemory.Text = $"{_settings.MaxMemoryMb} ��";
        TxtBadgeRam.Text = $"RAM: {_settings.MaxMemoryMb} ��";
        TxtWidth.Text = _settings.WindowWidth.ToString();
        TxtHeight.Text = _settings.WindowHeight.ToString();
        ChkFullscreen.IsChecked = _settings.Fullscreen;
        ChkSnapshots.IsChecked = _settings.ShowSnapshots;
        ChkCloseOnLaunch.IsChecked = _settings.CloseLauncherOnStart;
        ChkShowConsole.IsChecked = _settings.ShowConsole;
        ChkHideStreams.IsChecked = _settings.HideStreams;
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
            ? $"����� � �������: {totalRam} ��. ��� ��������� ���� ������ ���������� 2048�4096 ��."
            : "��� ��������� ���� ������ ���������� 2048�4096 ��.";
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
        _settings.HideStreams = ChkHideStreams.IsChecked == true;
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

        // ������ ������ ��������� ������ ����� �� ������� ��������
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
                    TxtBadgeJava.Text = "Java: �� �������";
                    TxtJavaList.Text = "Java �� ����������. ������� ������� ������ ������ �������������.";
                }
                else
                {
                    TxtBadgeJava.Text = $"Java {list[0].MajorVersion}";
                    TxtJavaList.Text = "�������:\n" + string.Join("\n", list.Select(j => "  � " + j));
                }
            });
        }
        catch (Exception ex) { Log.Warn("������ ������ Java: " + ex.Message); }
    }

    // =====================================================================
    //  ������� ���
    // =====================================================================

    // ---------- ���� ----------

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
        AppendLog($"���� ��������: {name}");
    }

    // ---------- ��� ���� ----------

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
            Title = "�������� ���� ��� ���� ��������",
            Filter = "�����������|*.png;*.jpg;*.jpeg;*.bmp;*.webp|��� �����|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        _settings.WindowBackgroundPath = dlg.FileName;
        TxtWindowBg.Text = dlg.FileName;
        ApplyWindowBackground();
        SettingsService.Save(_settings);
        AppendLog("���������� ��� ��������: " + Path.GetFileName(dlg.FileName));
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

    // ---------- ���� ���� ----------

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
                _settings.BackgroundStyle = (s as FrameworkElement)?.Tag?.ToString() ?? "�������";
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
            catch (Exception ex) { Log.Warn("�� ������� ��������� ������: " + ex.Message); }
        }

        ImgCustomBanner.Source = null;
        ImgCustomBanner.Visibility = Visibility.Collapsed;
    }

    private void BtnPickBanner_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "�������� ��� �������",
            Filter = "�����������|*.png;*.jpg;*.jpeg;*.bmp|��� �����|*.*"
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
    //  ������ � ������
    // =====================================================================

    private async Task LoadVersionsAsync()
    {
        try
        {
            SetStage("�������� �������� ������ Mojang...");
            ShowProgress(indeterminate: true);
            _manifest = await _versions.GetManifestAsync();

            var supported = VersionService.FilterSupported(_manifest, _settings.ShowSnapshots);
            AppendLog($"�������� ��������: {_manifest.Versions.Count} ������, �������� {supported.Count} (?1.16.5).");
        }
        catch (Exception ex)
        {
            AppendLog("������ �������� ������: " + ex.Message);
            TxtBannerInfo.Text = "�� ������� �������� ������ ������. ��������� ��������.";
        }
        finally { HideProgress(); }
    }

    private void LoadInstances()
    {
        _instances = InstanceService.LoadAll();

        if (!InstanceService.Loaded)
        {
            AppendLog("��������: ������ ������ �� ��������, ��������� �� �����������. " +
                      "������������� �������.");
            MessageBox.Show(
                "�� ������� ��������� ������ ������.\n\n" +
                "����� �� �������� ������, ���������� ��������� �� �����������.\n" +
                "����� ������ �� ����� �� �������.",
                "������ ������", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ����� �� ����� ����, � � ������ �� ��� � ���������������
        var orphans = InstanceService.ScanOrphans(_instances);
        if (orphans.Count > 0)
        {
            _instances.AddRange(orphans);
            InstanceService.SaveAll(_instances);
            AppendLog($"������� ������ �� �����: {orphans.Count}.");
        }

        // ��������� ������ ������ ������ ���� �������� ������� ����������.
        // ����� (��� ����) ������ ��� � ����� ����� ������������ ������.
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
                AppendLog($"������� ��������� ������ �{inst.Name}�.");
            }
        }
        else if (_instances.Count == 0 && _manifest is null)
        {
            AppendLog("��� ���������� � Mojang � ������ ������ ����������. " +
                      "������ �� ���������, ������������ ������ ���������.");
        }

        RefreshInstanceLists();
        VerifyInstalledVersions();
    }

    /// <summary>
    /// ������� ������ � ������� �� �����: ���� ������ ������ (������, ���������),
    /// �������� ������ ��� ��������� �������������, � �� ����� ������ �.
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
            AppendLog($"������� �������� �������: {string.Join(", ", missing)}. " +
                      "����� ��������� ��� ������� ������ܻ.");
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
            TxtBannerVersion.Text = "��� ������";
            TxtBannerInfo.Text = "�������� ������ �� ������� �������.";
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

    /// <summary>��� �� ������ � ������� �������� �, ����� ���������� ����.</summary>
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

        var dlg = new TextInputDialog("������������� ������",
            $"����� �������� ��� �{_selectedInstance.Name}�:", _selectedInstance.Name) { Owner = this };

        if (dlg.ShowDialog() != true) return;

        var name = dlg.Value.Trim();
        if (name.Length == 0) return;

        _selectedInstance.Name = name;
        InstanceService.SaveAll(_instances);

        var id = _selectedInstance.Id;
        RefreshInstanceLists();
        var restored = _instances.FirstOrDefault(i => i.Id == id);
        if (restored is not null) { CbInstances.SelectedItem = restored; SelectInstance(restored); }

        AppendLog($"������ �������������: �{name}�");
    }

    private void CtxMemory_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;

        var current = _selectedInstance.MaxMemoryMb > 0
            ? _selectedInstance.MaxMemoryMb.ToString()
            : _settings.MaxMemoryMb.ToString();

        var dlg = new TextInputDialog("������ ������",
            "������� �� �������� ���� ������? (0 � ��� � ����� ����������)", current) { Owner = this };

        if (dlg.ShowDialog() != true) return;

        if (!int.TryParse(dlg.Value.Trim(), out var mb) || mb < 0)
        {
            MessageBox.Show("������� �����, �������� 4096.", "������������ ��������",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _selectedInstance.MaxMemoryMb = mb;
        InstanceService.SaveAll(_instances);
        FillInstanceSettings(_selectedInstance);

        AppendLog(mb > 0
            ? $"��� �{_selectedInstance.Name}� ������ {mb} ��."
            : $"��� �{_selectedInstance.Name}� ������ ��� � ����� ����������.");
    }
    private void SelectInstance(GameInstance inst)
    {
        _selectedInstance = inst;
        _settings.LastInstanceId = inst.Id;

        TxtBannerVersion.Text = inst.Name;
        var installed = File.Exists(GamePaths.ForInstance(inst).VersionJar(inst.McVersion));
        TxtBannerInfo.Text = installed
            ? $"Minecraft {inst.McVersion} � ������ � �������"
            : $"Minecraft {inst.McVersion} � ����� ��������� � �������� Mojang";

        TxtBadgeLoader.Text = inst.LoaderDisplay;

        // ������
        TxtInstName.Text = inst.Name;
        TxtInstVersion.Text = "Minecraft " + inst.McVersion;
        TxtInstLoader.Text = inst.LoaderDisplay;
        TxtInstPlaytime.Text = inst.TotalPlaySeconds > 0 ? "� ����: " + inst.PlayTimeDisplay : "��� �� �����������";

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

    // ---------- �������������� ��������� ������ ----------

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

    // ---------- ������� ����� ----------

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
                $"{p} � {ModProfileService.CountMods(_selectedInstance, p)}");
            TxtProfileInfo.Text = "�����: " + string.Join("  �  ", counts);
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
            MessageBox.Show("������ ������ �������, ���� ������ ��������.",
                "���� ��������", MessageBoxButton.OK, MessageBoxImage.Warning);
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

            AppendLog($"������� �����: �{target}�");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "�� ������� ����������� �������",
                MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshModProfiles();
        }
    }

    private void BtnNewModProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;

        var dlg = new TextInputDialog("����� ������� �����",
            "�������� �������:", "��������: ��� ������") { Owner = this };

        if (dlg.ShowDialog() != true) return;

        var name = dlg.Value.Trim();
        if (name.Length == 0) return;

        var copy = MessageBox.Show(
            "����������� ������� ���� � ����� �������?\n\n���� � ������� ������.",
            "����� �������", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        try
        {
            ModProfileService.Create(_selectedInstance, name, copy);
            RefreshModProfiles();
            AppendLog($"������ ������� ����� �{name}�.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "������", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnDeleteModProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null || CbModProfile.SelectedItem is not string name) return;

        if (MessageBox.Show($"������� ������� �{name}� �� ����� ��� ������?",
                "�������� �������", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        try
        {
            ModProfileService.Delete(_selectedInstance, name);
            RefreshModProfiles();
            AppendLog($"������� �{name}� �����.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "������", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- �������� ����������� ----------

    private async void BtnCheckIntegrity_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;

        TxtInstHealth.Text = "���������";
        TxtInstHealth.Foreground = (Brush)FindResource("FgMuted");

        try
        {
            var svc = new IntegrityService(_versions);
            svc.Status += s => Dispatcher.BeginInvoke(() => TxtInstHealth.Text = s);

            var report = await svc.CheckAsync(_selectedInstance);

            var sb = new StringBuilder();
            sb.AppendLine(report.Summary);

            foreach (var p in report.Problems) sb.AppendLine("  ?  " + p);
            foreach (var w in report.Warnings) sb.AppendLine("  !  " + w);
            if (report.Problems.Count == 0)
                foreach (var o in report.Ok.Take(4)) sb.AppendLine("  ?  " + o);

            TxtInstHealth.Text = sb.ToString().TrimEnd();
            TxtInstHealth.Foreground = (Brush)FindResource(
                report.IsHealthy ? "Accent" : "Danger");

            if (report.Fixable.Count > 0)
            {
                var r = MessageBox.Show(
                    $"������� �������: {report.Problems.Count}.\n\n" +
                    "������� ����������� �����, ����� ������� ������ �� ������?",
                    "��������������", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (r == MessageBoxResult.Yes)
                {
                    var removed = IntegrityService.Repair(_selectedInstance, report);
                    TxtInstHealth.Text = $"������� ����������� ���������: {removed}. " +
                                         "������� ������ܻ � ����� ���������� ������.";
                    AppendLog($"�������������� ������: ������� {removed} ���������.");
                }
            }
        }
        catch (Exception ex)
        {
            TxtInstHealth.Text = "������ ��������: " + ex.Message;
            TxtInstHealth.Foreground = (Brush)FindResource("Danger");
        }
    }

    // ---------- ���������� ����� ----------

    private async void BtnCheckModUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;

        var inst = _selectedInstance;
        var modsDir = InstanceService.ModsDir(inst);

        TxtInstHealth.Text = "�������� ���������� �����";
        TxtInstHealth.Foreground = (Brush)FindResource("FgMuted");

        try
        {
            var progress = new Progress<string>(s =>
                Dispatcher.BeginInvoke(() => TxtInstHealth.Text = s));

            var updates = await _mods.CheckUpdatesAsync(
                modsDir, inst.McVersion, inst.Loader, progress);

            if (updates.Count == 0)
            {
                TxtInstHealth.Text = "��� ���� ���������.";
                TxtInstHealth.Foreground = (Brush)FindResource("Accent");
                return;
            }

            var list = string.Join("\n", updates.Take(12).Select(u =>
                $"  � {u.Project.Title}: {u.CurrentVersion} > {u.NewVersion}"));

            if (updates.Count > 12) list += $"\n  � � ��� {updates.Count - 12}";

            var r = MessageBox.Show(
                $"�������� ����������: {updates.Count}\n\n{list}\n\n�������� ���?",
                "���������� �����", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (r != MessageBoxResult.Yes)
            {
                TxtInstHealth.Text = $"�������� ����������: {updates.Count}";
                return;
            }

            var done = 0;
            foreach (var u in updates)
            {
                TxtInstHealth.Text = $"�������� {u.Project.Title}� ({done + 1} �� {updates.Count})";
                if (await _mods.ApplyUpdateAsync(u, modsDir, inst.McVersion, inst.Loader)) done++;
            }

            TxtInstHealth.Text = $"��������� �����: {done} �� {updates.Count}.";
            TxtInstHealth.Foreground = (Brush)FindResource("Accent");

            NotifyFinished("���� ���������", $"��������� {done} �����");
            RefreshInstanceStats();
            RefreshContent();
        }
        catch (Exception ex)
        {
            TxtInstHealth.Text = "������: " + ex.Message;
            TxtInstHealth.Foreground = (Brush)FindResource("Danger");
        }
    }

    // ---------- ��������� ----------

    private void BtnFindConflicts_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;

        TxtInstHealth.Text = "����� �����";

        try
        {
            var mods = ModInspector.ReadAll(InstanceService.ModsDir(_selectedInstance));

            if (mods.Count == 0)
            {
                TxtInstHealth.Text = "����� � ������ ���.";
                TxtInstHealth.Foreground = (Brush)FindResource("FgMuted");
                return;
            }

            var conflicts = ModInspector.FindConflicts(mods, _selectedInstance.Loader);

            if (conflicts.Count == 0)
            {
                TxtInstHealth.Text = $"��������� �����: {mods.Count}. ���������� �� �������.";
                TxtInstHealth.Foreground = (Brush)FindResource("Accent");
                return;
            }

            var sb = new StringBuilder($"��������� �����: {mods.Count}\n");

            foreach (var c in conflicts)
            {
                sb.AppendLine($"  {(c.IsError ? "?" : "!")}  {c.Title}");
                sb.AppendLine($"      {c.Details}");
                if (c.Files.Count > 0)
                    sb.AppendLine($"      �����: {string.Join(", ", c.Files.Take(4))}");
            }

            TxtInstHealth.Text = sb.ToString().TrimEnd();
            TxtInstHealth.Foreground = (Brush)FindResource(
                conflicts.Any(c => c.IsError) ? "Danger" : "FgMuted");
        }
        catch (Exception ex)
        {
            TxtInstHealth.Text = "������: " + ex.Message;
        }
    }

    // ---------- ���������� ----------

    private void RefreshStatistics()
    {
        if (_selectedInstance is null) return;

        var inst = _selectedInstance;

        TxtStatTotal.Text = inst.TotalPlaySeconds > 0 ? inst.PlayTimeDisplay : "�";
        TxtStatSessions.Text = inst.Sessions.Count.ToString();

        TxtStatAvg.Text = inst.Sessions.Count > 0
            ? FormatMinutes((long)inst.Sessions.Average(s => s.Seconds))
            : "�";

        // ������ �� 14 ����
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
                    : $"{x.Day:dd.MM}: �� ������"
            };
        }).ToList();
    }

    private static string FormatMinutes(long seconds)
    {
        if (seconds < 60) return $"{seconds} �";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours} � {ts.Minutes} ���" : $"{ts.Minutes} ���";
    }

    // ---------- JVM-������� ----------

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

        AppendLog($"������ �{_selectedInstance.Name}�: ������ JVM �{name}�.");
    }

    // ---------- ������ ������ ----------

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
            catch (Exception ex) { Log.Warn("������ ������: " + ex.Message); }
        }

        ImgInstIcon.Source = null;
        InstIconDot.Visibility = Visibility.Visible;
    }

    private void BtnInstIcon_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;

        var dlg = new OpenFileDialog
        {
            Title = "������ ������",
            Filter = "�����������|*.png;*.jpg;*.jpeg;*.bmp;*.ico|��� �����|*.*"
        };

        if (dlg.ShowDialog(this) != true) return;

        try
        {
            // �������� � ����� ������, ����� ������ �� ���������� ��� ��������
            var dst = Path.Combine(InstanceService.InstanceDir(_selectedInstance),
                "icon" + Path.GetExtension(dlg.FileName));

            File.Copy(dlg.FileName, dst, true);

            _selectedInstance.IconPath = dst;
            InstanceService.SaveAll(_instances);

            RefreshInstanceIcon();
            RefreshInstanceLists();
            AppendLog($"������ ������ �{_selectedInstance.Name}� ���������.");
        }
        catch (Exception ex)
        {
            MessageBox.Show("�� ������� ���������� ������: " + ex.Message,
                "������", MessageBoxButton.OK, MessageBoxImage.Error);
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
            Title = "java.exe ��� ���� ������",
            Filter = "java.exe|java.exe;javaw.exe|����������� ����� (*.exe)|*.exe"
        };
        if (dlg.ShowDialog(this) != true) return;

        var probe = JavaService.Probe(dlg.FileName, "instance");
        if (probe is null)
        {
            MessageBox.Show("�� ������� ���������� ������ Java �� ����� ����.",
                "Java", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _selectedInstance.JavaPath = dlg.FileName;
        TxtInstJava.Text = dlg.FileName;
        InstanceService.SaveAll(_instances);
        AppendLog($"��� �{_selectedInstance.Name}� ������� {probe}");
    }

    private void BtnInstSetRu_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;

        var ok = GameOptionsService.SetLanguage(
            InstanceService.InstanceDir(_selectedInstance), _selectedInstance.McVersion, "ru");

        MessageBox.Show(ok
                ? "������� ���� ������� � options.txt ���� ������."
                : "�� ������� �������� ���� � ����������� � �������.",
            "���� ����", MessageBoxButton.OK,
            ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void BtnDuplicateInstance_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;

        var src = _selectedInstance;

        var copy = new GameInstance
        {
            Name = src.Name + " (�����)",
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
            "����������� ����, ���������� � ������� � ����� ������?\n\n" +
            "���� � ������� ������ ������ � ���� �� �����������.",
            "������������", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (r == MessageBoxResult.Cancel) return;

        if (r == MessageBoxResult.Yes)
        {
            try
            {
                foreach (var sub in new[] { "mods", "resourcepacks", "shaderpacks", "config" })
                {
                    var from = Path.Combine(InstanceService.InstanceDir(src), sub);
                    if (Directory.Exists(from))
                        CopyDirectory(from, Path.Combine(InstanceService.InstanceDir(copy), sub));
                }
            }
            catch (Exception ex)
            {
                AppendLog("������ ����������� �����������: " + ex.Message);
            }
        }

        _instances.Add(copy);
        InstanceService.SaveAll(_instances);
        RefreshInstanceLists();
        CbInstances.SelectedItem = _instances.FirstOrDefault(i => i.Id == copy.Id);

        AppendLog($"������� ����� ������: �{copy.Name}�");
    }

    private void BtnResetInstance_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null) return;

        if (MessageBox.Show(
                "�������� �������������� ��������� ���� ������?\n\n" +
                "������, ������ ����, Java � ��������� �������� � ����� ���������.\n" +
                "���� � ���� �� ����������.",
                "����� �������� ������", MessageBoxButton.YesNo, MessageBoxImage.Question)
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
        AppendLog($"��������� ������ �{inst.Name}� ��������.");
    }
    private void RefreshInstanceStats()
    {
        if (_selectedInstance is null) return;

        var st = InstanceService.GetStats(_selectedInstance);

        TxtCountMods.Text = Plural(st.Mods, "����", "�����", "������");
        TxtCountRp.Text = Plural(st.ResourcePacks, "���", "����", "�����");
        TxtCountShaders.Text = Plural(st.ShaderPacks, "���", "����", "�����");
        TxtCountWorlds.Text = Plural(st.Worlds, "���", "����", "�����");
        TxtInstSize.Text = st.SizeDisplay;

        TxtQuickMods.Text = st.Mods > 0 ? $"���� ({st.Mods})" : "����";
        TxtQuickRp.Text = st.ResourcePacks > 0 ? $"���������� ({st.ResourcePacks})" : "����������";
        TxtQuickShaders.Text = st.ShaderPacks > 0 ? $"������� ({st.ShaderPacks})" : "�������";
        TxtQuickShots.Text = st.Screenshots > 0 ? $"��������� ({st.Screenshots})" : "���������";
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
                bmp.DecodePixelWidth = 264;      // �������� ������ �� ������
                bmp.UriSource = new Uri(f.FullName);
                bmp.EndInit();
                bmp.Freeze();

                items.Add(new { Thumb = bmp, Path = f.FullName, Name = f.Name });
            }
            catch { /* ����� ���� ���������� */ }
        }

        ItemsScreenshots.ItemsSource = items;
    }

    private void Screenshot_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string path) return;

        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { AppendLog("�� ������� ������� ��������: " + ex.Message); }
    }

    private void ChkSnapshots_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.ShowSnapshots = ChkSnapshots.IsChecked == true;
    }

    private void ChkHideStreams_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.HideStreams = ChkHideStreams.IsChecked == true;
        NavStreams.Visibility = _settings.HideStreams ? Visibility.Collapsed : Visibility.Visible;
        if (_settings.HideStreams) _twitchStream.StopMonitoring();
        else _twitchStream.StartMonitoring(_twitchAccount);
    }

    // ---------- �������� / �������� ----------

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
        AppendLog($"������� ������ �{inst.Name}� ({inst.McVersion}, {inst.LoaderDisplay}).");

        NavInstances.IsChecked = true;

        // ���� ��������� �� ������� � ������������� ��� ����������
        if (!string.IsNullOrEmpty(dlg.ModpackPath))
            _ = InstallModpackAsync(inst, dlg.ModpackPath!);
    }

    /// <summary>������������� ������ � ������ ��� ��������� ������.</summary>
    private async Task InstallModpackAsync(GameInstance inst, string packPath)
    {
        SetBusy(true);

        try
        {
            SetStage("������������ ������...");
            var info = await _modpacks.InstallAsync(packPath, inst);

            RefreshInstanceStats();
            RefreshContent();

            MessageBox.Show(
                $"������ �{info.Name}� ���������� � ������ �{inst.Name}�.\n\n" +
                $"������: {info.McVersion} {info.Loader.Display()}\n" +
                $"������: {info.FileCount}\n\n" +
                "��������� ����������� ��� ������ �������.",
                "������ �����", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error("��������� �������", ex);
            MessageBox.Show("�� ������� ���������� ������:\n\n" + ex.Message,
                "������", MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show("������ ������� ������, ���� ��� ��������. ������� ���������� ����.",
                "������ ������", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var inst = _selectedInstance;
        var r = MessageBox.Show(
            $"������� ������ �{inst.Name}�?\n\n" +
            "��� � ������� ������ � ������, ������ � �����������.\n" +
            "���� � ������ �� ������, ����� ��������.",
            "�������� ������", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        if (r == MessageBoxResult.Cancel) return;

        try
        {
            if (r == MessageBoxResult.Yes) InstanceService.Delete(inst, true);

            _instances.Remove(inst);
            InstanceService.SaveAll(_instances);
            _selectedInstance = null;
            RefreshInstanceLists();
            AppendLog($"������ �{inst.Name}� �������.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "������ ��������", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- ����� ----------

    private void OpenInstanceFolder(Func<GameInstance, string> selector)
    {
        if (_selectedInstance is null)
        {
            MessageBox.Show("������� �������� ������.", "������ �� �������",
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
            MessageBox.Show(ex.Message, "������", MessageBoxButton.OK, MessageBoxImage.Error);
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
    //  �������
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
            Log.Warn("�� ������� ��������� ����������� �������: " + ex.Message);
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

            StatusText = checking ? "���������" : online ? "������" : "������",
            StatusColor = new SolidColorBrush(checking
                ? (Color)ColorConverter.ConvertFromString("#8B93A3")
                : online ? ThemeService.CurrentAccent : (Color)ColorConverter.ConvertFromString("#F87171")),
            StatusBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                checking ? "#22262E" : online ? "#14301F" : "#2A1A1D")),

            Players = online ? status!.OnlinePlayers.ToString() : "�",
            Motd = checking ? "������� ������ �������..."
                : online ? (string.IsNullOrWhiteSpace(status!.Motd) ? srv.Description : status.Motd)
                : (status?.Error ?? "������ ����������"),

            VersionInfo = online && !string.IsNullOrEmpty(status!.VersionName)
                ? status.VersionName
                : "������ " + srv.RequiredVersion,
            PingInfo = online ? $"{status!.PingMs} ��" : "",

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
            AppendLog($"����� {addr} ���������� � ����� ������.");
        }
        catch (Exception ex) { AppendLog("�� ������� �����������: " + ex.Message); }
    }

    /// <summary>�������� �� �������� �������: ��������� ������ ������ ������ � ��������� � ������������.</summary>
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
                $"��� ������� {srv.Name} ����� ������ {srv.RequiredVersion}, " +
                "�� ���������� ������ ���.\n\n������� � ������?",
                "����� ������", MessageBoxButton.YesNo, MessageBoxImage.Question);

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

        AppendLog($"�������� ������ {dlg.Result.Name} ({dlg.Result.Address}).");
        _ = RefreshServersAsync();
    }

    // =====================================================================
    //  ������ ����
    // =====================================================================

    private async void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null)
        {
            MessageBox.Show("������� �������� ��� �������� ������.", "������ �� �������",
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
                "������� ������� � ������� �� ������� ��������.\n\n" +
                "�������� ���� ����� Microsoft � �������-�������.",
                "��������� ����", MessageBoxButton.OK, MessageBoxImage.Information);
            NavAccount.IsChecked = true;
            return;
        }

        // ������ �� ���������� �������
        if (!_settings.AllowMultipleInstances && _sessions.AnyRunning)
        {
            var running = _sessions.Sessions.First(s => s.IsRunning);
            MessageBox.Show(
                $"���� ��� ��������: �{running.InstanceName}�.\n\n" +
                "���������� � ������� ����������ܻ ���� ���������\n" +
                "��������� ����� � ����������.",
                "���� ��� ��������", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_sessions.IsInstanceRunning(inst.Id))
        {
            MessageBox.Show($"������ �{inst.Name}� ��� ��������.", "��� ��������",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PersistSettings();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true);

        try
        {
            // 1. �����
            if (!_account.IsOffline && _account.IsExpired &&
                !string.IsNullOrEmpty(_account.MicrosoftRefreshToken))
            {
                SetStage("�������� ������ Microsoft...");
                _account = await _auth.RefreshAsync(_account.MicrosoftRefreshToken!, ct);
                AccountStorage.Save(_account);
                SetAccount(_account, refreshSkin: false);
            }

            // 2. ���������: ����� ���� ������������� ��� ���� ������
            var paths = GamePaths.ForInstance(inst);
            paths.EnsureAll();

            _versions.Paths = paths;
            _downloads.Paths = paths;
            _loaders.Paths = paths;
            _loaders.InstallRoot = paths.IsIsolated
                ? Path.Combine(InstanceService.InstanceDir(inst), ".minecraft")
                : LauncherPaths.Root;

            if (paths.IsIsolated) AppendLog($"������ �{inst.Name}� �����������: ����� � � �����.");

            // 3. ������� ������
            SetStage($"����� �������� ������ {inst.McVersion}...");
            var manifest = _manifest ?? await _versions.GetManifestAsync(ct);
            var mv = manifest.Versions.FirstOrDefault(v => v.Id == inst.McVersion)
                     ?? throw new InvalidOperationException($"������ {inst.McVersion} �� ������� � ���������.");
            var baseDetail = await _versions.GetVersionDetailAsync(mv, ct);

            // 3. Java
            var requiredJava = baseDetail.JavaVersion?.MajorVersion ?? JavaService.RequiredJavaFor(inst.McVersion);
            SetStage($"�������� Java {requiredJava}...");

            JavaInstallation java;
            var javaOverride = !string.IsNullOrWhiteSpace(inst.JavaPath) ? inst.JavaPath : _settings.CustomJavaPath;

            if (!string.IsNullOrWhiteSpace(javaOverride) && File.Exists(javaOverride))
            {
                java = JavaService.Probe(javaOverride, "custom")
                       ?? throw new InvalidOperationException("��������� java.exe �� ��������.");
                if (java.MajorVersion < requiredJava)
                    AppendLog($"��������: ������� Java {java.MajorVersion}, ����� {requiredJava}.");
            }
            else
            {
                java = await _java.EnsureJavaAsync(requiredJava, ct);
            }

            Dispatcher.Invoke(() => TxtBadgeJava.Text = $"Java {java.MajorVersion}");

            // 4. ��������� ����� (����� � ����������)
            await Task.Run(() => _downloads.InstallVersionAsync(baseDetail, ct), ct);

            // 5. ���������
            var launchId = inst.EffectiveVersionId;

            if (inst.Loader != LoaderKind.Vanilla)
            {
                var alreadyInstalled = !string.IsNullOrEmpty(inst.LaunchVersionId) &&
                                       File.Exists(paths.VersionJson(inst.LaunchVersionId!));

                if (!alreadyInstalled)
                {
                    SetStage($"������������ {inst.Loader.Display()} {inst.LoaderVersion}...");
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

            // 6. �������� ������� (�� �������� inheritsFrom) � ��� �����
            SetStage("������� ����� �������...");
            var finalDetail = await _versions.ResolveAsync(launchId, ct);
            var install = await Task.Run(() => _downloads.InstallVersionAsync(finalDetail, ct), ct);

            NotifyFinished("�������� ���������", $"�{inst.Name}� ������ � �������");

            // 7. ������
            SetStage("�������� Minecraft...");
            InstanceService.EnsureFolders(inst);

            // ������� ���� �� ������� � ��� � TLegacy. ������������ options.txt �� �������.
            if (_settings.AutoSetGameLanguage)
            {
                var created = GameOptionsService.EnsureLanguage(
                    InstanceService.InstanceDir(inst), inst.McVersion, _settings.GameLanguage);
                if (created)
                    AppendLog($"���� ���� ����������: " +
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

            AppendLog($"Minecraft ������� (PID {proc.Id}), ������ �{inst.Name}�.");
            if (serverAddress is not null) AppendLog($"��������������� � ������� {serverAddress}.");
            SetStage("���� ��������");

            if (_settings.CloseLauncherOnStart)
            {
                await Task.Delay(2500, ct);
                Application.Current.Shutdown();
                return;
            }

            if (_settings.MinimizeOnLaunch) WindowState = WindowState.Minimized;

            // ����� ���������� �����
            var exitedFast = await Task.Run(() => proc.WaitForExit(9000), ct);
            if (exitedFast && proc.ExitCode != 0)
            {
                WindowState = WindowState.Normal;
                Activate();
                AppendLog($"���� ����������� ����� � ����� {proc.ExitCode}.");
                MessageBox.Show(
                    $"Minecraft ���������� � ����� {proc.ExitCode}.\n�������� ��������� ��� �������.",
                    "���� �� �����������", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("�������� ��������.");
            SetStage("��������");
        }
        catch (Exception ex)
        {
            Log.Error("������ �������", ex);
            MessageBox.Show(ex.Message, "������ �������", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStage("������");
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
        SetStage("������...");
    }

    // ---------- ��������� ----------

    private async void BtnStopGame_Click(object sender, RoutedEventArgs e)
    {
        _sessions.Prune();
        var running = _sessions.Sessions.Where(s => s.IsRunning).ToList();
        if (running.Count == 0) { UpdateRunStateUi(); return; }

        if (_settings.ConfirmGameStop)
        {
            var names = string.Join(", ", running.Select(s => s.InstanceName));
            var r = MessageBox.Show(
                $"������� ����: {names}?\n\n������������� �������� ����� ���� �������.",
                "���������� ����", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
        }

        BtnStopGame.IsEnabled = false;
        BtnStopGame.Content = "�����������ޅ";

        try
        {
            foreach (var s in running)
            {
                AppendLog($"������������ �{s.InstanceName}� (PID {s.Pid})...");
                await _sessions.StopAsync(s);
            }
        }
        finally
        {
            BtnStopGame.IsEnabled = true;
            BtnStopGame.Content = "����������";
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
                    TxtInstPlaytime.Text = "� ����: " + inst.PlayTimeDisplay;
                    RefreshInstanceStats();
                    LoadScreenshots();
                }
            }

            AppendLog($"--- �{session.InstanceName}� ����������� (��� {code}), " +
                      $"����� ������ {session.UptimeDisplay} ---");

            if (WindowState == WindowState.Minimized && !_sessions.AnyRunning)
                WindowState = WindowState.Normal;

            UpdateRunStateUi();
        });
    }

    /// <summary>����������� ������ ������ܻ / ����������ܻ � ����� � ���������.</summary>
    private void UpdateRunStateUi()
    {
        _sessions.Prune();

        var anyRunning = _sessions.AnyRunning;
        var thisRunning = _selectedInstance is not null &&
                          _sessions.IsInstanceRunning(_selectedInstance.Id);

        // ������ �������� ��������, ����� ���� ��� � ������������ ��������
        var hidePlay = !_busy && anyRunning && (!_settings.AllowMultipleInstances || thisRunning);

        BtnPlay.Visibility = hidePlay ? Visibility.Collapsed : Visibility.Visible;
        BtnStopGame.Visibility = anyRunning ? Visibility.Visible : Visibility.Collapsed;

        BtnPlay.IsEnabled = !_busy;
        BtnPlay.Content = _busy ? "�����������"
            : _selectedInstance is not null && !File.Exists(GamePaths.ForInstance(_selectedInstance).VersionJar(_selectedInstance.McVersion))
                ? "���������� � ������"
                : "������";

        RunningBadge.Visibility = anyRunning ? Visibility.Visible : Visibility.Collapsed;
        BtnDeleteInstance.IsEnabled = !thisRunning;

        UpdateUptimeBadge();
    }

    private void UpdateUptimeBadge()
    {
        var running = _sessions.Sessions.Where(s => s.IsRunning).ToList();
        if (running.Count == 0) return;

        TxtRunningBadge.Text = running.Count == 1
            ? $"{running[0].InstanceName} � {running[0].UptimeDisplay}"
            : $"�������� ���: {running.Count}";

        BtnStopGame.Content = running.Count > 1 ? $"���������� ({running.Count})" : "����������";
    }

    // =====================================================================
    //  �������
    // =====================================================================

    private void TxtOfflineName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;

        var name = TxtOfflineName.Text;
        if (string.IsNullOrWhiteSpace(name))
        {
            TxtOfflineHint.Text = "������� ������� (3-16 ��������).";
            TxtOfflineHint.Foreground = (Brush)FindResource("FgMuted");
            return;
        }

        if (OfflineAccountService.TryValidateName(name, out var error))
        {
            TxtOfflineHint.Text = "UUID �����: " + Dashed(OfflineAccountService.GenerateOfflineUuid(name.Trim()));
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

            TxtOfflineHint.Text = "�������-������� ������.";
            TxtOfflineHint.Foreground = (Brush)FindResource("Accent");
            AppendLog($"������ �������-�������: {acc.Username} ({acc.DashedUuid})");
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
        TxtSideName.Text = "Не выполнен вход";
        TxtSideStatus.Text = "Оффлайн";
        ImgSkinPreview.Source = null;
        ImgBannerSkin.Source = null;
        ImgAvatar.Source = null;
        TxtSkinPlaceholder.Visibility = Visibility.Visible;

        BtnUploadSkin.IsEnabled = false;
        BtnResetSkin.IsEnabled = false;

        TxtOfflineName.Clear();
        TxtOfflineHint.Text = "Введите никнейм (3-16 символов).";
        TxtOfflineHint.Foreground = (Brush)FindResource("FgMuted");
        TxtSkinStatus.Text = "";

        AppendLog("�������� ����� �� ��������.");
    }

    private async void BtnTwitchLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_twitchAuth.IsLoggingIn) return;

        BtnTwitchLogin.IsEnabled = false;
        BtnTwitchLogin.Content = "��������...";
        SetStage("����������� ����� Twitch...");

        try
        {
            var account = await _twitchAuth.AuthenticateAsync();
            if (account != null)
            {
                _twitchAccount = account;
                TwitchStorage.Save(account);
                _twitchStream.StartMonitoring(account);
                UpdateTwitchUI();
                UpdateStreamInfoDisplay(null);
                AppendLog($"Twitch: ����������� ��� {account.Username}");
            }
            else
            {
                AppendLog("Twitch: ����������� �������� ��� �� �������.");
            }
        }
catch (Exception ex)
                {
                    AppendLog("Не удалось обновить сессию: " + ex.Message);
                }
        finally
        {
            BtnTwitchLogin.IsEnabled = true;
            HideProgress();
        }
    }

    private void UpdateTwitchUI()
    {
        if (_twitchAccount != null)
        {
            TwitchAuthPanel.Visibility = Visibility.Collapsed;
            TwitchAccountPanel.Visibility = Visibility.Visible;
            TwitchAccountName.Text = _twitchAccount.Username;
            TwitchAccountStatus.Text = "�� ������������ ����� Twitch";

            if (!string.IsNullOrEmpty(_twitchAccount.ProfileImageUrl))
            {
                try
                {
                    TwitchAccountAvatar.Source = new BitmapImage(new Uri(_twitchAccount.ProfileImageUrl));
                }
                catch { TwitchAccountAvatar.Source = null; }
            }
        }
        else
        {
            TwitchAuthPanel.Visibility = Visibility.Visible;
            TwitchAccountPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateStreamInfoDisplay(TwitchStreamInfo? info)
    {
        if (PageStreams == null) return;

        if (_twitchAccount == null)
        {
            StreamsAuthRequired.Visibility = Visibility.Visible;
            StreamsContent.Visibility = Visibility.Collapsed;
            return;
        }

        StreamsAuthRequired.Visibility = Visibility.Collapsed;
        StreamsContent.Visibility = Visibility.Visible;

        if (info == null)
        {
            StreamsStatus.Text = "��������...";
            StreamsStatus.Foreground = (Brush)FindResource("FgMuted");
            StreamsTitle.Text = "";
            StreamsGame.Text = "";
            StreamsViewers.Text = "";
            BtnOpenStream.IsEnabled = false;
            return;
        }

        if (info.IsLive)
        {
            StreamsStatus.Text = "? � �����";
            StreamsStatus.Foreground = (Brush)FindResource("Danger");
            StreamsTitle.Text = info.Title;
            StreamsGame.Text = $"����: {info.GameName}";
            StreamsViewers.Text = $"��������: {info.ViewerCount}";
            BtnOpenStream.IsEnabled = true;
        }
        else
        {
            StreamsStatus.Text = "����� ������ �������";
            StreamsStatus.Foreground = (Brush)FindResource("FgMuted");
            StreamsTitle.Text = "";
            StreamsGame.Text = "";
            StreamsViewers.Text = "";
            BtnOpenStream.IsEnabled = false;
        }
    }

    private void BtnOpenStream_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStreamInfo?.IsLive == true)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _currentStreamInfo.StreamUrl,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    private async void BtnTwitchLogout_Click(object sender, RoutedEventArgs e)
    {
        _twitchStream.StopMonitoring();
        TwitchStorage.Clear();
        _twitchAccount = null;
        _currentStreamInfo = null;
        UpdateTwitchUI();
        UpdateStreamInfoDisplay(null);
        AppendLog("Twitch: выполнен выход из аккаунта.");
    }

    private async Task RefreshStreamStatusAsync()
    {
        if (_twitchAccount == null)
        {
            UpdateStreamInfoDisplay(null);
            return;
        }
        var info = await _twitchStream.GetStreamInfoAsync().ConfigureAwait(false);
        Dispatcher.Invoke(() => UpdateStreamInfoDisplay(info));
    }

private void SetAccount(MinecraftAccount acc, bool refreshSkin)
    {
        _account = acc;

        TxtAccName.Text = acc.Username;
        TxtAccUuid.Text = acc.DashedUuid;

        if (acc.IsOffline)
        {
            TxtSideStatus.Text = "Оффлайн-профиль";
        }
        else
        {
            TxtSideStatus.Text = acc.IsExpired ? "Сессия истекла" : "Microsoft · онлайн";
        }

        TxtSideName.Text = acc.Username;
        BtnUploadSkin.IsEnabled = !acc.IsOffline;
        BtnResetSkin.IsEnabled = !acc.IsOffline;

        if (acc.IsOffline)
        {
            TxtSkinStatus.Text = "����� ����� ���������� ��� �������-�������.";
            TxtSkinStatus.Foreground = (Brush)FindResource("FgMuted");
        }

        if (refreshSkin) _ = LoadSkinImagesAsync(acc);
    }

    private async Task LoadSkinImagesAsync(MinecraftAccount acc)
    {
        try
        {
            var body = await _skins.GetBodyRenderAsync(acc);
            var avatar = await _skins.GetAvatarAsync(acc, 72);

            Dispatcher.Invoke(() =>
            {
                if (body is not null)
                {
                    var img = ToImage(body);
                    ImgSkinPreview.Source = img;
                    ImgBannerSkin.Source = img;
                    TxtSkinPlaceholder.Visibility = Visibility.Collapsed;
                }
                if (avatar is not null) ImgAvatar.Source = ToImage(avatar);
            });
        }
        catch (Exception ex) { Log.Warn("�� ������� ��������� ����: " + ex.Message); }
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
        TxtSkinStatus.Text = "�������� ������...";
        await LoadSkinImagesAsync(_account);
        TxtSkinStatus.Text = "������ ���������.";
    }

    private void BtnBrowseSkin_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "�������� ���� �����",
            Filter = "PNG ����������� (*.png)|*.png|��� ����� (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != true) return;

        TxtSkinPath.Text = dlg.FileName;

        try
        {
            SkinService.ValidateSkinPng(File.ReadAllBytes(dlg.FileName));
            TxtSkinStatus.Text = "���� ����������. ������� �������� �����.";
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
            TxtSkinStatus.Text = "������� �������� PNG-���� �����.";
            TxtSkinStatus.Foreground = (Brush)FindResource("Danger");
            return;
        }

        BtnUploadSkin.IsEnabled = false;
        TxtSkinStatus.Foreground = (Brush)FindResource("FgMuted");
        TxtSkinStatus.Text = "��������� ���� �� ������� Mojang...";

        try
        {
            if (_account.IsOffline)
                throw new InvalidOperationException(
                    "����� ����� �������� ������ ��� �������� Microsoft.");

            if (_account.IsExpired && !string.IsNullOrEmpty(_account.MicrosoftRefreshToken))
            {
                _account = await _auth.RefreshAsync(_account.MicrosoftRefreshToken!);
                AccountStorage.Save(_account);
                SetAccount(_account, refreshSkin: false);
            }

            var model = RbSlim.IsChecked == true ? SkinService.SkinModel.Slim : SkinService.SkinModel.Classic;
            await _skins.UploadSkinAsync(_account.AccessToken, path, model);

            TxtSkinStatus.Text = "���� �������! �������� ������...";
            TxtSkinStatus.Foreground = (Brush)FindResource("Accent");

            await Task.Delay(2500);
            await LoadSkinImagesAsync(_account);
            TxtSkinStatus.Text = "���� ������� �������.";
        }
        catch (Exception ex)
        {
            Log.Error("������ ����� �����", ex);
            TxtSkinStatus.Text = ex.Message;
            TxtSkinStatus.Foreground = (Brush)FindResource("Danger");
        }
        finally { BtnUploadSkin.IsEnabled = true; }
    }

    private async void BtnResetSkin_Click(object sender, RoutedEventArgs e)
    {
        if (_account is null) return;

        if (MessageBox.Show("�������� ���� �� �����������?", "�������������",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        BtnResetSkin.IsEnabled = false;
        try
        {
            await _skins.ResetSkinAsync(_account.AccessToken);
            TxtSkinStatus.Text = "���� �������.";
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
    //  ������
    // =====================================================================

    // =====================================================================
    //  ���������: ������� � ��������
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

    /// <summary>����� ��������� ��������� � ����� ��������� �� ����.</summary>
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
        TxtJavaList.Text = "�����";
        _ = Task.Run(DetectJava);
    }

    private void BtnClearJava_Click(object sender, RoutedEventArgs e)
    {
        TxtJavaPath.Clear();
        _settings.CustomJavaPath = "";
        PersistSettings();
        _ = Task.Run(DetectJava);
    }

    // ---------- ���������� �������� ���� ----------

    private List<InstalledVersion> _installedVersions = new();

    // ---------- ���������� � ����� �� �������� ----------

    private string _currentSettingsSection = "game";

    // ��������� ���� ��������� � ���������, ����� �� ������ ���� �� ������ �����
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

    /// <summary>����������� ����������, ��� ��������� ��������.</summary>
    private void ShowSavedHint()
    {
        if (TxtSettingsHint is null) return;

        TxtSettingsHint.Text = $"��������� � {DateTime.Now:HH:mm:ss}";
        TxtSettingsHint.Foreground = (Brush)FindResource("Accent");

        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (s, _) =>
        {
            t.Stop();
            TxtSettingsHint.Text = "��������� ����������� � ����������� �����";
            TxtSettingsHint.Foreground = (Brush)FindResource("FgMuted");
        };
        t.Start();
    }

    private void BtnResetSection_Click(object sender, RoutedEventArgs e)
    {
        var sectionName = _currentSettingsSection switch
        {
            "java" => "�Java � �������",
            "view" => "�������� ���",
            "storage" => "����������",
            "versions" => "������� �����",
            "maint" => "�������������",
            _ => "�����"
        };

        if (_currentSettingsSection is "versions" or "maint")
        {
            MessageBox.Show($"� ������� {sectionName} ��� �������� ��� ������.",
                "�����", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show($"�������� ��������� ������� {sectionName} � ��������� �� ���������?",
                "����� �������", MessageBoxButton.YesNo, MessageBoxImage.Question)
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

        AppendLog($"������ {sectionName} �������.");
    }

    // ---------- ���� �������� ����� ----------

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

        AppendLog("��������� ���� �������� �����.");
    }
    private void BtnScanVersions_Click(object sender, RoutedEventArgs e) => ScanVersions();

    private void ScanVersions()
    {
        TxtVersionsSummary.Text = "���������";

        try
        {
            _installedVersions = VersionManagerService.Scan(_instances);

            var total = _installedVersions.Sum(v => v.SizeBytes);
            TxtVersionsSummary.Text = _installedVersions.Count == 0
                ? "������ ��� �� �����������. ��� �������� ����� ������� ������� ����."
                : $"����� ������: {_installedVersions.Count}  �  ������ {Human(total)}";

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
                if (v.IsIsolated) parts.Add($"������������� � {v.OwnerInstance}");
                if (!v.HasJar) parts.Add("������ �� ��������");
                if (v.InheritsFrom is not null) parts.Add($"�� ���� {v.InheritsFrom}");
                parts.Add(v.InUse ? "������������: " + string.Join(", ", v.UsedBy) : "�� ������������");

                return new
                {
                    v.Id,
                    v.Kind,
                    Dir = v.Directory,
                    Info = string.Join("  �  ", parts),
                    KindBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg)),
                    KindFg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg))
                };
            }).ToList();
        }
        catch (Exception ex)
        {
            TxtVersionsSummary.Text = "������ ������������: " + ex.Message;
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
            MessageBox.Show("������� ���������� ���� � ����� ������ ������ ������.",
                "���� ��������", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var warn = version.InUse
            ? $"\n\n��������: ������ ���������� ������: {string.Join(", ", version.UsedBy)}.\n" +
              "����� �������� ��� ������� ����� ������ ��� �������."
            : "";

        var r = MessageBox.Show(
            $"������� ������ �{version.Id}�?\n\n" +
            $"����������� {version.SizeDisplay}.\n" +
            "����, ���� � ��������� ������ ��������� �� �����." + warn,
            "�������� ������", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (r != MessageBoxResult.Yes) return;

        try
        {
            var freed = VersionManagerService.Delete(version);
            AppendLog($"������ {version.Id} �������, ����������� {Human(freed)}.");
            ScanVersions();
        }
        catch (Exception ex)
        {
            MessageBox.Show("�� ������� �������: " + ex.Message + "\n\n" +
                            "��������, ����� ������ ������ ����������.",
                "������", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void BtnCalcSize_Click(object sender, RoutedEventArgs e)
    {
        TxtStorageInfo.Text = "�������";

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
                perInstance.Add($"     � {inst.Name}: {Human(s)}" + (inst.Isolated ? "  (�������������)" : ""));
            }

            var text =
                $"����� ���������:\n" +
                $"     ����������: {Human(libs)}\n" +
                $"     �������: {Human(assets)}\n" +
                $"     ������: {Human(versions)}\n" +
                $"     Java: {Human(runtime)}\n" +
                $"     ���: {Human(cache)}\n\n" +
                $"������ ({_instances.Count}): {Human(instancesTotal)}\n" +
                string.Join("\n", perInstance) +
                $"\n\n�����: {Human(libs + assets + versions + runtime + cache + instancesTotal)}";

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
                    // �������� ������ ��������� � �� ����� � �������
                    if (f.EndsWith("version_manifest_v2.json", StringComparison.OrdinalIgnoreCase)) continue;
                    try { freed += new FileInfo(f).Length; File.Delete(f); } catch { }
                }
            }

            TxtMaintenance.Text = $"��� ������, ����������� {Human(freed)}.";
            AppendLog($"��� ������ ({Human(freed)}).");
        }
        catch (Exception ex)
        {
            TxtMaintenance.Text = "�� ������� �������� ���: " + ex.Message;
        }
    }

    private async void BtnCheckCurse_Click(object sender, RoutedEventArgs e)
    {
        TxtMaintenance.Text = "�������� ������ � CurseForge API�";

        var ok = await _mods.CheckCurseForgeAsync();

        TxtMaintenance.Text = ok
            ? "CurseForge API �������� � ����� �������� �� ����� ����������."
            : _mods.CurseForgeError ?? "CurseForge ����������, ������������ ������ Modrinth.";

        UpdateModsSubtitle();
    }

    private void BtnResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(
            "�������� ��� ��������� �������� � ��������� �� ���������?\n\n" +
            "������, ���� � ������� ��������� �� �����.",
            "����� ��������", MessageBoxButton.YesNo, MessageBoxImage.Warning);

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

        TxtMaintenance.Text = "��������� ��������.";
        AppendLog("��������� �������� � ��������� �� ���������.");
    }

    // =====================================================================
    //  ����
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

    /// <summary>��������� ��������� ��������� ��� �������.</summary>
    private void UpdatePager(ModService.SearchPage page)
    {
        ModPager.Visibility = page.TotalCount > ModPageSize ? Visibility.Visible : Visibility.Collapsed;
        TxtPageInfo.Text = $"�������� {page.PageNumber} �� {page.TotalPages}";
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

    /// <summary>����� ������ � ������ � ������ ��������.</summary>
    private void RunModSearchFromStart()
    {
        _modOffset = 0;
        RunModSearch();
    }

    private void RunModSearch() => BtnModSearch_Click(this, new RoutedEventArgs());

    /// <summary>������ ������: ����� ������ � ������ ��������.</summary>
    private void BtnModSearchNew_Click(object sender, RoutedEventArgs e) => RunModSearchFromStart();

    private async void BtnModSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null)
        {
            TxtModStatus.Text = "������� �������� ������ � �� �� ������� ������ ���� � ���������.";
            return;
        }

        _modCts?.Cancel();
        _modCts = new CancellationTokenSource();
        var ct = _modCts.Token;

        BtnModSearch.IsEnabled = false;
        var type = SelectedContentType;
        TxtModStatus.Text = "���";
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
                    ? $"������ �� �������. � ������ �{_selectedInstance.Name}� ��� ���������� � " +
                      "��� ����� �������� ������ � Fabric, Forge ��� NeoForge."
                    : $"������ �� ������� ��� Minecraft {_selectedInstance.McVersion}.";
                UpdatePager(page);
                return;
            }

            var extra = _mods.CurseForgeAvailable ? "" : "  �  ������ Modrinth";
            TxtModStatus.Text = $"�������: {page.TotalCount}  �  " +
                                $"{_selectedInstance.McVersion} � {_selectedInstance.Loader.Display()}{extra}";

            ItemsMods.ItemsSource = _modResults.Select((m, i) => BuildModView(m, i)).ToList();
            UpdatePager(page);
            ModScroll.ScrollToTop();

            _ = LoadModIconsAsync(_modResults.ToList(), ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            TxtModStatus.Text = "������ ������: " + ex.Message;
            Log.Error("����� �����", ex);
        }
        finally { BtnModSearch.IsEnabled = true; }
    }

    private object BuildModView(ModSearchResult m, int index)
    {
        // ������ ��, ��� ��� � ����. ��������� ���������� ���������� (LoadModIconsAsync),
        // ����� WPF ������ �������� ����� � UI-������ � ���� ������.
        var icon = ImageCacheService.TryGetCached(m.IconUrl);

        var isModrinth = m.Provider == ModProvider.Modrinth;

        return new
        {
            Index = index,
            m.Title,
            Summary = string.IsNullOrWhiteSpace(m.Summary) ? "��� ��������" : m.Summary,
            Icon = icon,
            Initial = m.Title.Length > 0 ? m.Title[..1].ToUpperInvariant() : "?",
            PlaceholderVisibility = icon is null ? Visibility.Visible : Visibility.Collapsed,
            Source = m.ProviderDisplay,
            SourceBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isModrinth ? "#14301F" : "#33210F")),
            SourceFg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isModrinth ? "#4ADE80" : "#FB923C")),
            DownloadsText = m.DownloadsDisplay + " ��������",
            AuthorText = string.IsNullOrEmpty(m.Author) ? "" : "�����: " + m.Author,
            PageUrl = m.PageUrl ?? ""
        };
    }

    /// <summary>
    /// ��������� ������ � ���� � ��������� ������, ����� ��� ������.
    /// ��� ������� ���������� ���������, � �������� ������������� ����������.
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
        catch (Exception ex) { AppendLog("�� ������� ������� ������: " + ex.Message); }
    }

    /// <summary>��������� �������� ���� �� ���������� ��������.</summary>
    private void ModPageInApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int index) return;
        if (index < 0 || index >= _modResults.Count) return;

        var project = _modResults[index];

        var dlg = new ModBrowserDialog(project) { Owner = this };
        var result = dlg.ShowDialog();

        // �� ���� ��������� ����� ����� ��������� ���
        if (result == true && dlg.InstallRequested)
            _ = InstallModAsync(project, null);
    }
    private async void ModInstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int index) return;
        if (index < 0 || index >= _modResults.Count) return;

        await InstallModAsync(_modResults[index], btn);
    }

    /// <summary>����� ���� ���������: �� �������� � �� ���� ���������.</summary>
    private async Task InstallModAsync(ModSearchResult project, Button? btn)
    {
        if (_selectedInstance is null)
        {
            MessageBox.Show("������� �������� ������.", "������ �� �������",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var inst = _selectedInstance;

        if (btn is not null) { btn.IsEnabled = false; btn.Content = "�"; }

        try
        {
            // ������ ������ ������ � ��� � Modrinth App
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

            var msg = $"�����������: {outcome.Installed.Count}";
            if (outcome.Skipped.Count > 0) msg += $"\n���������: {string.Join(", ", outcome.Skipped)}";
            if (outcome.Failed.Count > 0) msg += $"\n������: {string.Join(", ", outcome.Failed)}";

            AppendLog($"�{project.Title}� > {msg.Replace("\n", "; ")}");

            MessageBox.Show(msg, project.Title,
                outcome.Failed.Count > 0 ? MessageBoxButton.OK : MessageBoxButton.OK,
                outcome.Failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            RefreshInstanceStats();
        }
        catch (Exception ex)
        {
            Log.Error("��������� ����", ex);
            MessageBox.Show(ex.Message, "������ ���������", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (btn is not null) { btn.IsEnabled = true; btn.Content = "����������"; }
        }
    }

    private void UpdateModsSubtitle()
    {
        if (TxtModsSubtitle is null) return;

        TxtModsSubtitle.Text = _mods.CurseForgeAvailable
            ? "������� Modrinth � CurseForge"
            : "������� Modrinth  �  CurseForge ���������� � ������� ������";
    }
    private void SldMemory_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        var mb = (int)e.NewValue;
        TxtMemory.Text = $"{mb} ��";
        TxtBadgeRam.Text = $"RAM: {mb} ��";
        _settings.MaxMemoryMb = mb;
    }

    private void BtnBrowseJava_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "�������� java.exe",
            Filter = "java.exe|java.exe;javaw.exe|����������� ����� (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != true) return;

        var probe = JavaService.Probe(dlg.FileName, "custom");
        if (probe is null)
        {
            MessageBox.Show("�� ������� ���������� ������ Java.", "Java",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        TxtJavaPath.Text = dlg.FileName;
        _settings.CustomJavaPath = dlg.FileName;
        TxtBadgeJava.Text = $"Java {probe.MajorVersion}";
        AppendLog("������� Java: " + probe);
    }

    private void BtnOpenDir_Click(object sender, RoutedEventArgs e)
    {
        try { InstanceService.OpenFolder(_settings.GameDir); }
        catch (Exception ex) { AppendLog("�� ������� ������� �����: " + ex.Message); }
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

        PageHome.Visibility = tag == "0" ? Visibility.Visible : Visibility.Collapsed;
        PageInstances.Visibility = tag == "1" ? Visibility.Visible : Visibility.Collapsed;
        PageServers.Visibility = tag == "2" ? Visibility.Visible : Visibility.Collapsed;
        PageAccount.Visibility = tag == "3" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = tag == "4" ? Visibility.Visible : Visibility.Collapsed;
        PageConsole.Visibility = tag == "5" ? Visibility.Visible : Visibility.Collapsed;
        PageMods.Visibility = tag == "6" ? Visibility.Visible : Visibility.Collapsed;

        PageContent.Visibility = tag == "7" ? Visibility.Visible : Visibility.Collapsed;
        PageBot.Visibility = tag == "8" ? Visibility.Visible : Visibility.Collapsed;
        PageStreams.Visibility = tag == "10" ? Visibility.Visible : Visibility.Collapsed;

        if (tag == "1") { RefreshInstanceStats(); LoadScreenshots(); }
        if (tag == "6")
        {
            UpdateModsSubtitle();
            if (_modResults.Count == 0 && _selectedInstance is not null) RunModSearchFromStart();
        }
        if (tag == "7") RefreshContent();
        if (tag == "8") RefreshBotEnvInfo();
        if (tag == "10") _ = RefreshStreamStatusAsync();
    }

    // =====================================================================
    //  ����� �� �������
    // =====================================================================

    private string _instanceFilter = "";

    private void TxtInstanceSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;

        _instanceFilter = TxtInstanceSearch.Text.Trim();
        RefreshInstanceLists();
    }

    /// <summary>���� ������ ����������, ����� ������ ���������� �����.</summary>
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
    //  ������ �������
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
            TxtLogInfo.Text = "������ �������� � ����� ����";
            ScrollLog.ScrollToEnd();
            return;
        }

        var lines = all.Split('\n');
        var filtered = lines.Where(l => MatchesLogLevel(l, _logFilter)).ToList();

        TxtLog.Text = filtered.Count > 0
            ? string.Join("\n", filtered)
            : (_logFilter == "error" ? "������ ���." : "�������������� ���.");

        TxtLogInfo.Text = $"�������� {filtered.Count} �� {lines.Length} �����";
        ScrollLog.ScrollToEnd();
    }

    private static bool MatchesLogLevel(string line, string level)
    {
        var lower = line.ToLowerInvariant();

        var isError = lower.Contains("[error]") || lower.Contains("error]") ||
                      lower.Contains("exception") || lower.Contains("������") ||
                      lower.Contains("�� �������") || lower.Contains("severe") ||
                      lower.Contains("fatal") || lower.Contains("!!!");

        if (level == "error") return isError;

        // ��� ��������������� ���������� � ������ � ��� ������
        return isError || lower.Contains("[warn]") || lower.Contains("warn]") ||
               lower.Contains("��������") || lower.Contains("����������");
    }

    // =====================================================================
    //  ������� �������
    // =====================================================================

    // =====================================================================
    //  ��������� ��˨�����
    // =====================================================================

    /// <summary>
    /// ComboBox � Slider �������� ������ ����: ������� ������ �� ������ ������
    /// ��� ��������� �������� � � ������ ������� �������� ��������.
    /// ����� �� ����� ����� ��������� � ������� ��������� ������������� ScrollViewer.
    /// </summary>
    private void BlockingControl_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not UIElement element) return;

        // � ���������� ������ ��������� ���� � �� ������
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
    /// ������ ������ �� ������ ������ �� ��� ComboBox � Slider ����.
    /// �������� ���� ��� ����� ��������.
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

            // ������ �������������� ������� �� �����
            if (child is not ComboBox) SetupWheelHandling(child);
        }
    }

    /// <summary>��������� ������ ������ �������� � ����� ������ ��� ���������.</summary>
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

        // � ����� ����� �� ������������� ������� �������
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

            // Ctrl+1..9 � ������������ �������
            case >= Key.D1 and <= Key.D9 when ctrl:
                SwitchTab(e.Key - Key.D1);
                e.Handled = true;
                break;
        }
    }

    private void SwitchTab(int index)
    {
        var navs = new[] { NavHome, NavInstances, NavMods, NavContent, NavServers,
                           NavBot, NavAccount, NavSettings, NavConsole, NavStreams };

        if (index >= 0 && index < navs.Length) navs[index].IsChecked = true;
    }

    /// <summary>F5 � ��������� ��, ��� ������� ������.</summary>
    private void RefreshCurrentPage()
    {
        if (PageInstances.Visibility == Visibility.Visible)
        {
            RefreshInstanceLists();
            RefreshInstanceStats();
            LoadScreenshots();
            AppendLog("������ ������ �������.");
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
    //  ����������� � ����������
    // =====================================================================

    /// <summary>
    /// ������ � ������ ����� � ����� ����, ���� ���� ������� �
    /// ����� �� ������ � �� �������� �� ��������-���.
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

            // ������� ������ � ������ �����
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
    //  �������� � ���
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
            var size = p.BytesTotal > 0 ? $"  �  {Human(p.BytesDone)} / {Human(p.BytesTotal)}" : "";
            var file = string.IsNullOrEmpty(p.CurrentFile) ? "" : "  �  " + Shorten(p.CurrentFile, 44);

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
            // ��� �������� ������� �������������� ������ ���������� ������
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
    //  ������� ������
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
            TxtContentStatus.Text = "������ �� �������.";
            ItemsContent.ItemsSource = null;
            return;
        }

        TxtContentSubtitle.Text = $"������ �{_selectedInstance.Name}� � " +
                                  $"{_selectedInstance.McVersion} � {_selectedInstance.LoaderDisplay}";

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
                        Info = $"{Human(size)} � ������� {d.LastWriteTime:dd.MM.yyyy HH:mm}",
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
                        ? Path.GetFileNameWithoutExtension(f.Name)
                        : Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(f.Name)) + "  (��������)";

                    items.Add(new
                    {
                        Name = display,
                        Info = $"{Human(f.Length)} � {f.LastWriteTime:dd.MM.yyyy}",
                        Path = f.FullName,
                        ToggleText = enabled ? "���������" : "��������",
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
            Log.Warn("������ ����������� ������: " + ex.Message);
        }

        ItemsContent.ItemsSource = items;

        var kindName = _contentKind switch
        {
            ContentKind.ResourcePacks => "�����������",
            ContentKind.Shaders => "��������",
            ContentKind.Worlds => "�����",
            _ => "�����"
        };

        TxtContentStatus.Text = items.Count == 0
            ? $"��� {kindName}. ���������� ����� ���� ��� ������� �������."
            : $"����� {kindName}: {items.Count}";
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
            MessageBox.Show("�� ������� �����������: " + ex.Message,
                "������", MessageBoxButton.OK, MessageBoxImage.Error);
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

        var name = Path.GetFileName(path);
        var isDir = Directory.Exists(path);

        var msg = isDir
            ? $"������� ��� �{name}�?\n\n��� �������� ����������."
            : $"������� �{name}�?";

        if (MessageBox.Show(msg, "��������", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        try
        {
            if (isDir) Directory.Delete(path, true);
            else File.Delete(path);

            AppendLog($"�������: {name}");
            RefreshContent();
            RefreshInstanceStats();
        }
        catch (Exception ex)
        {
            MessageBox.Show("�� ������� �������: " + ex.Message,
                "������", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- ������ ----------

    private void BtnImportMod_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null)
        {
            MessageBox.Show("������� �������� ������.", "������ �� �������",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = "�������� ����, ���������� ��� �������",
            Filter = "��� ��������������|*.jar;*.zip;*.mrpack|���� (*.jar)|*.jar|������ (*.zip)|*.zip|��� �����|*.*",
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
                    var worldDst = Path.Combine(InstanceService.SavesDir(inst), Path.GetFileName(src));
                    if (Directory.Exists(worldDst)) { skipped.Add(Path.GetFileName(src) + " (��� ����)"); continue; }
                    CopyDirectory(src, worldDst);
                    ok++;
                    continue;
                }

                if (!File.Exists(src)) continue;

                var ext = Path.GetExtension(src).ToLowerInvariant();
                var name = Path.GetFileName(src);

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
                    // ������ ������ ������� � ������� ������
                    _ = InstallModpackAsync(inst, src);
                    ok++;
                    continue;
                }
                else
                {
                    skipped.Add(name + " (����������� ���)");
                    continue;
                }

                var dst = Path.Combine(dstDir, name);
                if (File.Exists(dst)) { skipped.Add(name + " (��� ����)"); continue; }

                File.Copy(src, dst);
                ok++;
            }
            catch (Exception ex)
            {
                failed.Add(Path.GetFileName(src) + ": " + ex.Message);
            }
        }

        var report = $"���������: {ok}";
        if (skipped.Count > 0) report += $"\n���������: {string.Join(", ", skipped)}";
        if (failed.Count > 0) report += $"\n������: {string.Join(", ", failed)}";

        AppendLog("������: " + report.Replace("\n", "; "));
        RefreshContent();
        RefreshInstanceStats();

        MessageBox.Show(report, "������ ������", MessageBoxButton.OK,
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
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), true);

        foreach (var dir in Directory.GetDirectories(src))
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }

    // ---------- �������������� ----------

    private void Content_DragOver(object sender, DragEventArgs e)
    {
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasFiles && _selectedInstance is not null ? DragDropEffects.Copy : DragDropEffects.None;

        if (hasFiles && _selectedInstance is not null)
        {
            DropHint.Visibility = Visibility.Visible;
            TxtDropTarget.Text = $"� ������ �{_selectedInstance.Name}�  �  .jar > ����, .zip > ���������� ��� �������";
        }

        e.Handled = true;
    }

    private void Content_Drop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (_selectedInstance is null)
        {
            MessageBox.Show("������� �������� ������.", "������ �� �������",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) ImportFiles(files);
        e.Handled = true;
    }

    // ---------- ������ �� ������ Modrinth ----------

    private async void BtnImportUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance is null)
        {
            MessageBox.Show("������� �������� ������.", "������ �� �������",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new TextInputDialog(
            "������ �� Modrinth",
            "�������� ������ �� ��� ��� ��� slug:",
            "https://modrinth.com/mod/sodium") { Owner = this };

        if (dlg.ShowDialog() != true) return;

        var input = dlg.Value.Trim();
        if (input.Length == 0) return;

        var slug = ExtractModrinthSlug(input);
        if (slug is null)
        {
            MessageBox.Show("�� ������� ���������� ������.\n\n������: https://modrinth.com/mod/sodium",
                "�������� ������", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetStage($"��� �{slug}� �� Modrinth...");

        try
        {
            var project = await _mods.GetProjectAsync(ModProvider.Modrinth, slug);
            if (project is null)
            {
                MessageBox.Show($"������ �{slug}� �� ������ �� Modrinth.",
                    "�� �������", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var verDlg = new ModVersionDialog(_mods, project,
                _selectedInstance.McVersion, _selectedInstance.Loader) { Owner = this };

            if (verDlg.ShowDialog() != true || verDlg.SelectedFile is null) return;

            var outcome = await _mods.InstallAsync(
                verDlg.SelectedFile, InstanceService.ModsDir(_selectedInstance),
                _selectedInstance.McVersion, _selectedInstance.Loader, verDlg.InstallDependencies);

            var msg = $"�����������: {outcome.Installed.Count}";
            if (outcome.Failed.Count > 0) msg += $"\n������: {string.Join(", ", outcome.Failed)}";

            MessageBox.Show(msg, project.Title, MessageBoxButton.OK, MessageBoxImage.Information);

            RefreshContent();
            RefreshInstanceStats();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "������ �������", MessageBoxButton.OK, MessageBoxImage.Error);
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
    //  ��� (mineflayer)
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

    /// <summary>���������� ������� ���������� ���� ��� ����� ����.</summary>
    private void SendBot(string command)
    {
        if (!_bots.AnyRunning)
        {
            OnBotOutput("[!] ������� ��������� ����");
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
            0 => "��� �����",
            1 => "1 ���",
            _ => $"{running} �����"
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
            ? "����������"
            : $"���������� � {target.Name.ToUpperInvariant()}";

        ItemsBots.ItemsSource = list.Select(b => new
        {
            b.Id,
            b.Name,
            Info = $"{b.Endpoint}  �  {(b.InWorld ? "� ����" : "������������")}  �  {b.UptimeDisplay}",
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
            ? "��������� ������: Node.js � mineflayer �����������."
            : "��� ������ ������� ������� ������� Node.js � mineflayer (~40 ��). " +
              $"Node.js: {(node ? "����" : "���")}, mineflayer: {(mf ? "����" : "���")}.";

        if (string.IsNullOrWhiteSpace(TxtBotOwner.Text) && _account is not null)
            TxtBotOwner.Text = _account.Username;

        RefreshBotList();

        if (CbBotVersion.ItemsSource is null)
            CbBotVersion.ItemsSource = BotService.SupportedVersions;

        if (string.IsNullOrWhiteSpace(BotVersionText) && _selectedInstance is not null)
        {
            // ����������� ������ ������, � ���� ��� ����� �������������� � ��������� �������
            var v = _selectedInstance.McVersion;
            CbBotVersion.Text = BotService.IsVersionSupported(v)
                ? v
                : BotService.SuggestVersion(v) ?? "";

            if (!BotService.IsVersionSupported(v))
                OnBotOutput($"[��������] Minecraft {v} ���� �� �������������� �����, " +
                            $"������� {CbBotVersion.Text}.");
        }
    }

    private string BotVersionText => (CbBotVersion.Text ?? "").Trim();

    // ---------- ����� ��������� ���� � ��������� ���� ----------

    private readonly LanDiscoveryService _lan = new();
    private List<LanWorld> _lanWorlds = new();

    private async void BtnFindLan_Click(object sender, RoutedEventArgs e)
    {
        BtnFindLan.IsEnabled = false;
        BtnFindLan.Content = "���";

        LanResults.Visibility = Visibility.Visible;
        TxtLanStatus.Text = "������ ��������� ���� 6 ������. ���������, ��� ��� ������ ��� ����.";
        ItemsLan.ItemsSource = null;

        try
        {
            _lanWorlds = await _lan.ScanOnceAsync(6000);

            if (_lanWorlds.Count == 0)
            {
                TxtLanStatus.Text =
                    "�������� ����� �� �������.\n\n" +
                    "���������: ���� ��������, � ���� Esc ������ �������� ��� ����, " +
                    "� ������� � ����� �� ����� ���������� ��� � ����� ����.\n" +
                    "���� ����� ������ ������� � �� ������� � ������� ����.";
                return;
            }

            TxtLanStatus.Text = $"������� �����: {_lanWorlds.Count}. �������, ����� ���������� �����.";

            ItemsLan.ItemsSource = _lanWorlds.Select(w => new
            {
                Key = $"{w.Address}:{w.Port}",
                Motd = w.Motd,
                Addr = $"{w.Address}:{w.Port}"
            }).ToList();

            // ������������ ��� ����������� �����
            if (_lanWorlds.Count == 1) ApplyLanWorld(_lanWorlds[0]);
        }
        catch (Exception ex)
        {
            TxtLanStatus.Text = "������ ������: " + ex.Message;
        }
        finally
        {
            BtnFindLan.IsEnabled = true;
            BtnFindLan.Content = "����� ��� ���";
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
        // ���� �� ��������� ������� ���������� ��� localhost
        var isLocal = GetLocalIps().Contains(world.Address);

        TxtBotHost.Text = isLocal ? "localhost" : world.Address;
        TxtBotPort.Text = world.Port.ToString();

        TxtLanStatus.Text = $"������ ��� �{world.Motd}� � ���� {world.Port}. " +
                            "������ ������� ���������� ����.";

        OnBotOutput($"[lan] ������ ��� �{world.Motd}� �� {TxtBotHost.Text}:{world.Port}");
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
            OnBotOutput("[setup] �������� ���������...");
            await _bots.EnsureEnvironmentAsync(OnProgress);
            RefreshBotEnvInfo();
        }
        catch (Exception ex)
        {
            OnBotOutput("[setup] ������: " + ex.Message);
            MessageBox.Show(ex.Message, "��������� ���������",
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
            MessageBox.Show("������� ���������� ���� (1�65535).\n\n" +
                            "���� ��������� ���� ����� � ���� ����� �������� ��� ����.",
                "������������ ����", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var name = TxtBotName.Text.Trim();
        if (!OfflineAccountService.TryValidateName(name, out var err))
        {
            MessageBox.Show(err, "��� ����", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BtnBotStart.IsEnabled = false;

        try
        {
            await _bots.StartAsync(host, port, name, BotVersionText);
            AppendLog($"��� {name} ������������ � {host}:{port}");
            RefreshBotList();
        }
        catch (Exception ex)
        {
            OnBotOutput("[error] " + ex.Message);
            MessageBox.Show(ex.Message, "�� ������� ��������� ����",
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
            MessageBox.Show("������� ���� ��� � ���� � �� ��� ���� ���������.",
                "����� ���", MessageBoxButton.OK, MessageBoxImage.Information);
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
    //  ������������
    // =====================================================================

    private List<MaintenanceService.TargetInfo> _maintTargets = new();
    private readonly HashSet<MaintenanceService.CleanTarget> _maintChecked = new();

    // ---------- ����������� ����� ----------

    private void RefreshPortableState()
    {
        if (TxtPortableState is null) return;

        if (LauncherPaths.IsPortable)
        {
            TxtPortableState.Text = $"�������. ������: {LauncherPaths.Root}";
            TxtPortableState.Foreground = (Brush)FindResource("Accent");
            BtnPortableToggle.Content = "��������� ����������� �����";
        }
        else
        {
            var can = LauncherPaths.CanUsePortable();

            TxtPortableState.Text = can
                ? $"��������. ������: {LauncherPaths.Root}"
                : "����������: ��� ���� �� ������ ����� � ���������. " +
                  "���������� exe � ������� ����� ��� �� ������.";

            TxtPortableState.Foreground = (Brush)FindResource(can ? "FgMuted" : "Danger");
            BtnPortableToggle.Content = "�������� ����������� �����";
            BtnPortableToggle.IsEnabled = can;
        }
    }

    private void BtnPortableToggle_Click(object sender, RoutedEventArgs e)
    {
        var turnOn = !LauncherPaths.IsPortable;

        if (_sessions.AnyRunning || _bots.AnyRunning)
        {
            MessageBox.Show("������� ���������� ���� � �����.", "������",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var question = turnOn
            ? "�������� ����������� �����?\n\n" +
              $"������ �������� �:\n{Path.Combine(LauncherPaths.ExeDir, "MaysLauncherData")}\n\n" +
              "����������� ���� ������� ������ � ���������?"
            : "��������� ����������� �����?\n\n" +
              "������ �������� � ����� ������������ (%APPDATA%).\n\n" +
              "����������� ���� ������� ������ � ���������?";

        var r = MessageBox.Show(question, "����������� �����",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (r == MessageBoxResult.Cancel) return;

        try
        {
            if (r == MessageBoxResult.Yes)
            {
                var copied = 0;
                LauncherPaths.MigrateTo(turnOn, _ => copied++);
                AppendLog($"����������� �����: ����������� ������ {copied}.");
            }

            LauncherPaths.SetPortable(turnOn);

            MessageBox.Show(
                "������. ��������� ������� � ���� ����� ����������� ��������.\n\n" +
                "������� ��� ������?",
                "����������� �����", MessageBoxButton.OK, MessageBoxImage.Information);

            var restart = MessageBox.Show("������� �������?", "����������",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (restart == MessageBoxResult.Yes) Application.Current.Shutdown();
            else RefreshPortableState();
        }
        catch (Exception ex)
        {
            MessageBox.Show("�� ������� ����������� �����: " + ex.Message,
                "������", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void BtnScanMaint_Click(object sender, RoutedEventArgs e) => ScanMaintenance();

    private void ScanMaintenance()
    {
        TxtMaintTotal.Text = "�������";

        _maintTargets = MaintenanceService.Enumerate();
        var total = MaintenanceService.TotalSize();

        TxtMaintTotal.Text = $"����� ������ ��������: {Human(total)}  �  {LauncherPaths.Root}";

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
            MessageBox.Show("��������, ��� ����� �������.", "������ �� �������",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_sessions.AnyRunning)
        {
            MessageBox.Show("������� ���������� ����.", "���� ��������",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selected = _maintTargets.Where(t => _maintChecked.Contains(t.Target)).ToList();
        var dangerous = selected.Where(t => t.Dangerous).ToList();
        var totalSize = selected.Sum(t => t.Size);

        var msg = "����� �������:\n\n" +
                  string.Join("\n", selected.Select(t => $"  � {t.Title} � {t.SizeDisplay}")) +
                  $"\n\n����������� �������� {Human(totalSize)}.";

        if (dangerous.Count > 0)
            msg += "\n\n��������: ����� ���������� ���� ������ � ������ � ������. " +
                   "������������ �� ����� ����������.";

        if (MessageBox.Show(msg + "\n\n����������?", "������������� �������",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var freed = MaintenanceService.Clean(selected);

        // ���-�� �� ��������� ����� ���� ��������� � ������
        if (_maintChecked.Contains(MaintenanceService.CleanTarget.Instances))
        {
            _instances.Clear();
            RefreshInstanceLists();
        }

        if (_maintChecked.Contains(MaintenanceService.CleanTarget.Account)) BtnLogout_Click(sender, e);
        if (_maintChecked.Contains(MaintenanceService.CleanTarget.ImageCache)) ImageCacheService.ClearMemory();

        _maintChecked.Clear();
        ScanMaintenance();

        MessageBox.Show($"������. ����������� {Human(freed)}.", "������� ���������",
            MessageBoxButton.OK, MessageBoxImage.Information);

        AppendLog($"�������: ����������� {Human(freed)}");
    }

    private void BtnReinstallSoft_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "����� ������� ������ ����, ����������, �������, Java � ���.\n\n" +
                "������ (����, ����, ���������), ������� � ��������� ����������.\n" +
                "����� ���� ��������� ������ ��� ��������� �������.\n\n����������?",
                "������������� �������", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        if (_sessions.AnyRunning)
        {
            MessageBox.Show("������� ���������� ����.", "���� ��������",
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

        MessageBox.Show($"������. ����������� {Human(freed)}.\n\n" +
                        "����� ���� ���������� ������ ��� ������� ������ܻ.",
            "�������������", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnReinstallFull_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "����� ������� ��� ������ ��������:\n\n" +
                "  � ������ ����, ����������, �������\n" +
                "  � ������ �� ����� ������ � ������\n" +
                "  � ������� � ���������\n\n" +
                "��� ���� �������� ���������. ������������ ������ ����� ������.\n\n����������?",
                "������ �������������", MessageBoxButton.YesNo, MessageBoxImage.Stop)
            != MessageBoxResult.Yes) return;

        if (MessageBox.Show("����� ������� ��� ���� � ����?", "��������� �������������",
                MessageBoxButton.YesNo, MessageBoxImage.Stop) != MessageBoxResult.Yes) return;

        if (_sessions.AnyRunning) _sessions.StopAllAsync().GetAwaiter().GetResult();
        _bots.StopAll();

        var freed = MaintenanceService.Clean(MaintenanceService.Enumerate());

        MessageBox.Show($"������� {Human(freed)}.\n\n������� ������ ���������. " +
                        "��������� ��� ������ � �� ����� ��� ����� ���������.",
            "������", MessageBoxButton.OK, MessageBoxImage.Information);

        Application.Current.Shutdown();
    }

    private void BtnUninstall_Click(object sender, RoutedEventArgs e)
    {
        var exePath = Environment.ProcessPath ?? "";
        var isExe = exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

        var r = MessageBox.Show(
            "��������� ������� MaysLauncher � ����������?\n\n" +
            $"����� ������� ����� ������:\n{LauncherPaths.Root}\n\n" +
            (isExe ? "��� � ������� � ��� ���� ��������.\n���� � ������� ������ ������.\n"
                   : "���� �������� ������� ������ (������� �� ��� exe).\n") +
            "\n��� �������� ����������.",
            "�������� ��������", MessageBoxButton.YesNoCancel, MessageBoxImage.Stop);

        if (r == MessageBoxResult.Cancel) return;

        var removeExe = isExe && r == MessageBoxResult.Yes;

        if (MessageBox.Show(
                removeExe
                    ? "������� ������ ��� ������ � ����, ����� ���������. �����������?"
                    : "������� ������ ��� ������ � ���������. �����������?",
                "��������� �������������", MessageBoxButton.YesNo, MessageBoxImage.Stop)
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
            MessageBox.Show("�� ������� ��������� ��������: " + ex.Message,
                "������", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private static string Human(long bytes)
    {
        string[] units = { "�", "��", "��", "��" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }

    private static string Shorten(string s, int max) =>
        s.Length <= max ? s : "�" + s[^(max - 1)..];
}
