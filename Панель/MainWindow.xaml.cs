using System.Windows;
using System.Windows.Controls;
using LauncherPanel.Models;
using LauncherPanel.Services;
using LauncherPanel.Views;

namespace LauncherPanel;

public partial class MainWindow : Window
{
    private readonly GitHubService _github;
    private LauncherConfig _config;

    private readonly NewsPage _newsPage;
    private readonly ServersPage _serversPage;
    private readonly ReleasePage _releasePage;
    private readonly SettingsPage _settingsPage;

    public MainWindow()
    {
        InitializeComponent();
        _github = new GitHubService();
        _config = new LauncherConfig();

        _newsPage = new NewsPage(_config);
        _serversPage = new ServersPage(_config);
        _releasePage = new ReleasePage(_github);
        _settingsPage = new SettingsPage(_github, (owner, repo) =>
        {
            _github.SetRepository(owner, repo);
        });

        _settingsPage.TokenSet += (token) =>
        {
            _github.SetToken(token);
        };

        Loaded += MainWindow_Loaded;
        MainContent.Content = _newsPage;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _config = _github.LoadLocalConfig();
        _newsPage.UpdateConfig(_config);
        _serversPage.UpdateConfig(_config);
        SetStatus("Локальная конфигурация загружена");
    }

    private void BtnNews_Click(object sender, RoutedEventArgs e)
    {
        HighlightButton(BtnNews);
        _newsPage.UpdateConfig(_config);
        MainContent.Content = _newsPage;
    }

    private void BtnServers_Click(object sender, RoutedEventArgs e)
    {
        HighlightButton(BtnServers);
        _serversPage.UpdateConfig(_config);
        MainContent.Content = _serversPage;
    }

    private void BtnRelease_Click(object sender, RoutedEventArgs e)
    {
        HighlightButton(BtnRelease);
        MainContent.Content = _releasePage;
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        HighlightButton(BtnSettings);
        MainContent.Content = _settingsPage;
    }

    private void HighlightButton(Button active)
    {
        foreach (var btn in new[] { BtnNews, BtnServers, BtnRelease, BtnSettings })
        {
            btn.Background = (btn == active) 
                ? FindResource("Accent") as System.Windows.Media.Brush 
                : FindResource("Panel") as System.Windows.Media.Brush;
            btn.Foreground = (btn == active) 
                ? System.Windows.Media.Brushes.Black 
                : FindResource("Text") as System.Windows.Media.Brush;
        }
    }

    private void BtnSaveLocal_Click(object sender, RoutedEventArgs e)
    {
        _newsPage.SaveChanges();
        _serversPage.SaveChanges();
        _github.SaveLocalConfig(_config);
        SetStatus("Сохранено локально");
    }

    private async void BtnPush_Click(object sender, RoutedEventArgs e)
    {
        _newsPage.SaveChanges();
        _serversPage.SaveChanges();
        _github.SaveLocalConfig(_config);

        SetStatus("Загрузка на GitHub...");
        BtnPush.IsEnabled = false;

        var result = await _github.PushConfigAsync(_config, "Обновление конфигурации из панели");

        BtnPush.IsEnabled = true;
        SetStatus(result ? "✅ Запушено на GitHub!" : "❌ Ошибка при пуше");
    }

    private void SetStatus(string text)
    {
        Dispatcher.Invoke(() => TxtStatus.Text = text);
    }
}
