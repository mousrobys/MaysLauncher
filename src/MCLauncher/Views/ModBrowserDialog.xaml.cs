using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using MCLauncher.Models;
using MCLauncher.Services;
using Microsoft.Web.WebView2.Core;

namespace MCLauncher.Views;

/// <summary>
/// Просмотр страницы мода прямо в лаунчере.
/// Использует WebView2 (движок Edge). Если рантайма нет — предлагаем
/// открыть в обычном браузере, а не падаем.
/// </summary>
public partial class ModBrowserDialog : Window
{
    private readonly ModSearchResult _project;
    private readonly string _url;

    /// <summary>Пользователь нажал «Установить» — MainWindow откроет выбор версии.</summary>
    public bool InstallRequested { get; private set; }

    public ModBrowserDialog(ModSearchResult project)
    {
        InitializeComponent();

        _project = project;
        _url = BuildUrl(project);

        TxtTitle.Text = project.Title;
        TxtUrl.Text = _url;
        TxtStatus.Text = $"{project.ProviderDisplay} · {project.DownloadsDisplay} загрузок";

        Loaded += OnLoadedAsync;
        Closed += (_, _) => { try { Browser.Dispose(); } catch { } };
    }

    private static string BuildUrl(ModSearchResult p)
    {
        if (!string.IsNullOrWhiteSpace(p.PageUrl)) return p.PageUrl!;

        var slug = !string.IsNullOrWhiteSpace(p.Slug) ? p.Slug : p.ProjectId;

        return p.Provider == ModProvider.Modrinth
            ? $"https://modrinth.com/mod/{slug}"
            : $"https://www.curseforge.com/minecraft/mc-mods/{slug}";
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        _ = LoadIconAsync();

        try
        {
            // Профиль храним в папке лаунчера, чтобы не мусорить в системе
            var userData = Path.Combine(LauncherPaths.CacheDir, "webview");
            Directory.CreateDirectory(userData);

            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await Browser.EnsureCoreWebView2Async(env);

            var core = Browser.CoreWebView2;

            // Лишние возможности браузера в лаунчере ни к чему
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;

            // Ссылки «в новом окне» открываем во внешнем браузере
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                OpenExternal(args.Uri);
            };

            core.NavigationStarting += (_, args) =>
            {
                TxtStatus.Text = "Загрузка…";
                TxtUrl.Text = args.Uri;
            };

            core.NavigationCompleted += (_, args) =>
            {
                BtnBack.IsEnabled = core.CanGoBack;
                BtnForward.IsEnabled = core.CanGoForward;

                TxtStatus.Text = args.IsSuccess
                    ? $"{_project.ProviderDisplay} · {_project.DownloadsDisplay} загрузок"
                    : "Не удалось загрузить страницу";

                if (!string.IsNullOrWhiteSpace(core.DocumentTitle))
                    TxtTitle.Text = core.DocumentTitle;
            };

            PanelFallback.Visibility = Visibility.Collapsed;
            Browser.Visibility = Visibility.Visible;

            Browser.Source = new Uri(_url);
        }
        catch (Exception ex)
        {
            ShowFallback(ex);
        }
    }

    private async Task LoadIconAsync()
    {
        var img = await ImageCacheService.GetAsync(_project.IconUrl, App.Http, 64);
        if (img is not null) Dispatcher.Invoke(() => ImgIcon.Source = img);
    }

    /// <summary>Без WebView2 показываем понятное объяснение вместо ошибки.</summary>
    private void ShowFallback(Exception ex)
    {
        Log.Warn("WebView2 недоступен: " + ex.Message);

        Browser.Visibility = Visibility.Collapsed;
        PanelFallback.Visibility = Visibility.Visible;

        TxtFallback.Text = "Встроенный просмотр недоступен";
        TxtFallbackHint.Text =
            "Не установлен компонент WebView2 (входит в Microsoft Edge).\n\n" +
            "Страницу можно открыть в обычном браузере — кнопка ниже.\n" +
            "Чтобы включить просмотр внутри лаунчера, установите " +
            "«Microsoft Edge WebView2 Runtime».";

        TxtStatus.Text = "WebView2 не найден";
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2?.CanGoBack == true) Browser.CoreWebView2.GoBack();
    }

    private void BtnForward_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2?.CanGoForward == true) Browser.CoreWebView2.GoForward();
    }

    private void BtnReload_Click(object sender, RoutedEventArgs e)
    {
        Browser.CoreWebView2?.Reload();
    }

    private void BtnExternal_Click(object sender, RoutedEventArgs e)
    {
        var url = Browser.CoreWebView2?.Source ?? _url;
        OpenExternal(url);
    }

    private static void OpenExternal(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Открытие ссылки: " + ex.Message); }
    }

    private void BtnInstall_Click(object sender, RoutedEventArgs e)
    {
        InstallRequested = true;
        DialogResult = true;
        Close();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal : WindowState.Maximized;
        else if (e.ClickCount == 1 && WindowState != WindowState.Maximized) DragMove();
    }
}
