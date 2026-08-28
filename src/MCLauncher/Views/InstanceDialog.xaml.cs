using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MCLauncher.Models;
using MCLauncher.Services;

namespace MCLauncher.Views;

public partial class InstanceDialog : Window
{
    private readonly VersionService _versions;
    private readonly ModLoaderService _loaders;

    private VersionManifest? _manifest;
    private LoaderKind _loader = LoaderKind.Vanilla;
    private string _iconColor = "#4ADE80";
    private CancellationTokenSource? _loaderCts;

    /// <summary>Итог: заполняется при успешном создании.</summary>
    public GameInstance? Result { get; private set; }

    /// <summary>Путь к .mrpack, который нужно распаковать после создания сборки.</summary>
    public string? ModpackPath { get; private set; }

    public InstanceDialog(VersionService versions, ModLoaderService loaders,
        bool showSnapshots, bool defaultIsolated = false)
    {
        InitializeComponent();

        _versions = versions;
        _loaders = loaders;
        ChkSnapshots.IsChecked = showSnapshots;
        ChkIsolated.IsChecked = defaultIsolated;

        BuildColorSwatches();
        Loaded += async (_, _) => await LoadVersionsAsync();
    }

    private void BuildColorSwatches()
    {
        var items = LauncherSettings.AccentPresets.Select(p => new
        {
            p.Hex,
            Brush = Frozen((Color)ColorConverter.ConvertFromString(p.Hex)),
            Border = p.Hex == _iconColor
                ? new SolidColorBrush(Colors.White)
                : new SolidColorBrush(Colors.Transparent)
        }).ToList();

        ItemsColors.ItemsSource = items;
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void Color_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string hex) return;
        _iconColor = hex;
        BuildColorSwatches();
    }

    private async Task LoadVersionsAsync()
    {
        try
        {
            TxtStatus.Text = "Загружаю список версий...";
            _manifest = await _versions.GetManifestAsync();
            FillVersions();
            TxtStatus.Text = "";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "Не удалось загрузить версии: " + ex.Message;
        }
    }

    private void FillVersions()
    {
        if (_manifest is null) return;

        var list = VersionService.FilterSupported(_manifest, ChkSnapshots.IsChecked == true);
        CbMcVersion.ItemsSource = list;
        if (CbMcVersion.SelectedItem is null && list.Count > 0) CbMcVersion.SelectedIndex = 0;
    }

    private void ChkSnapshots_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        FillVersions();
    }

    private void Loader_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        _loader = (sender as FrameworkElement)?.Tag?.ToString() switch
        {
            "1" => LoaderKind.Fabric,
            "2" => LoaderKind.Forge,
            "3" => LoaderKind.NeoForge,
            _ => LoaderKind.Vanilla
        };

        PanelLoaderVersion.Visibility = _loader == LoaderKind.Vanilla
            ? Visibility.Collapsed
            : Visibility.Visible;

        UpdateSuggestedName();
        _ = LoadLoaderVersionsAsync();
    }

    private void CbMcVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateSuggestedName();
        _ = LoadLoaderVersionsAsync();
    }

    private bool _nameEditedByUser;

    private void TxtName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded && TxtName.IsKeyboardFocusWithin) _nameEditedByUser = true;
    }

    private void UpdateSuggestedName()
    {
        if (_nameEditedByUser) return;
        if (CbMcVersion.SelectedItem is not ManifestVersion mv) return;

        TxtName.Text = _loader == LoaderKind.Vanilla
            ? mv.Id
            : $"{mv.Id} {_loader.Display()}";
    }

    private async Task LoadLoaderVersionsAsync()
    {
        _loaderCts?.Cancel();
        _loaderCts = new CancellationTokenSource();
        var ct = _loaderCts.Token;

        if (_loader == LoaderKind.Vanilla)
        {
            CbLoaderVersion.ItemsSource = null;
            TxtLoaderHint.Text = "";
            return;
        }

        if (CbMcVersion.SelectedItem is not ManifestVersion mv) return;

        CbLoaderVersion.ItemsSource = null;
        TxtLoaderHint.Text = $"Ищу версии {_loader.Display()} для {mv.Id}...";
        BtnCreate.IsEnabled = false;

        try
        {
            var list = await _loaders.GetLoaderVersionsAsync(_loader, mv.Id, ct);
            if (ct.IsCancellationRequested) return;

            if (list.Count == 0)
            {
                TxtLoaderHint.Text = $"{_loader.Display()} пока не поддерживает {mv.Id}. " +
                                     "Выберите другую версию игры или загрузчик.";
                BtnCreate.IsEnabled = false;
                return;
            }

            CbLoaderVersion.ItemsSource = list;
            CbLoaderVersion.SelectedIndex = 0;

            TxtLoaderHint.Text = $"Найдено версий: {list.Count}. " +
                                 (_loader == LoaderKind.Fabric
                                     ? "Fabric ставится мгновенно."
                                     : "Установщик запустится при первом старте — это займёт около минуты.");
            BtnCreate.IsEnabled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            TxtLoaderHint.Text = "Ошибка получения версий: " + ex.Message;
            BtnCreate.IsEnabled = true;
        }
    }

    private void BtnCreate_Click(object sender, RoutedEventArgs e)
    {
        if (CbMcVersion.SelectedItem is not ManifestVersion mv)
        {
            TxtStatus.Text = "Выберите версию Minecraft.";
            return;
        }

        var name = TxtName.Text.Trim();
        if (name.Length == 0)
        {
            TxtStatus.Text = "Введите название сборки.";
            TxtName.Focus();
            return;
        }

        string? loaderVersion = null;
        if (_loader != LoaderKind.Vanilla)
        {
            if (CbLoaderVersion.SelectedItem is not LoaderVersion lv)
            {
                TxtStatus.Text = "Выберите версию загрузчика.";
                return;
            }
            loaderVersion = lv.Version;
        }

        Result = new GameInstance
        {
            Name = name,
            McVersion = mv.Id,
            Loader = _loader,
            LoaderVersion = loaderVersion,
            LaunchVersionId = _loader == LoaderKind.Vanilla ? mv.Id : null,
            IconColor = _iconColor,
            Isolated = ChkIsolated.IsChecked == true
        };

        DialogResult = true;
        Close();
    }

    // ---------- Импорт модпака ----------

    private void BtnImportPack_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите модпак",
            Filter = "Модпаки Modrinth (*.mrpack)|*.mrpack|Архивы (*.zip)|*.zip|Все файлы|*.*"
        };

        if (dlg.ShowDialog(this) != true) return;
        ApplyPack(dlg.FileName);
    }

    private async void BtnPackUrl_Click(object sender, RoutedEventArgs e)
    {
        var input = new TextInputDialog("Модпак по ссылке",
            "Прямая ссылка на файл .mrpack:",
            "https://cdn.modrinth.com/data/.../pack.mrpack") { Owner = this };

        if (input.ShowDialog() != true) return;

        var url = input.Value.Trim();
        if (url.Length == 0) return;

        TxtStatus.Text = "Скачиваю модпак…";
        BtnCreate.IsEnabled = false;

        try
        {
            var packs = new ModpackService(App.Http);
            var path = await packs.DownloadPackAsync(url);
            ApplyPack(path);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "Не удалось скачать: " + ex.Message;
        }
        finally { BtnCreate.IsEnabled = true; }
    }

    /// <summary>Читает модпак и подставляет его параметры в форму.</summary>
    private void ApplyPack(string path)
    {
        try
        {
            var packs = new ModpackService(App.Http);
            var info = packs.ReadInfo(path);

            ModpackPath = path;

            TxtName.Text = info.Name + (info.Version.Length > 0 ? $" {info.Version}" : "");
            _nameEditedByUser = true;

            // Версия игры из модпака
            var target = (CbMcVersion.ItemsSource as IEnumerable<ManifestVersion>)?
                .FirstOrDefault(v => v.Id == info.McVersion);

            if (target is null && _manifest is not null)
            {
                // Версии может не быть в отфильтрованном списке — показываем снапшоты
                ChkSnapshots.IsChecked = true;
                FillVersions();
                target = (CbMcVersion.ItemsSource as IEnumerable<ManifestVersion>)?
                    .FirstOrDefault(v => v.Id == info.McVersion);
            }

            if (target is not null) CbMcVersion.SelectedItem = target;

            // Загрузчик
            var rb = info.Loader switch
            {
                LoaderKind.Fabric => RbFabric,
                LoaderKind.Forge => RbForge,
                LoaderKind.NeoForge => RbNeoForge,
                _ => RbVanilla
            };
            rb.IsChecked = true;

            TxtStatus.Text = $"Модпак «{info.Name}»: {info.McVersion}, " +
                             $"{info.Loader.Display()}, файлов {info.FileCount}." +
                             (target is null ? "  Версия игры не найдена в манифесте!" : "");
        }
        catch (Exception ex)
        {
            ModpackPath = null;
            TxtStatus.Text = "Не удалось прочитать модпак: " + ex.Message;
        }
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
