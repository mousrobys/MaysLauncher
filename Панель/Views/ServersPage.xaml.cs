using System.Windows;
using System.Windows.Controls;
using LauncherPanel.Models;
using LauncherPanel.Services;

namespace LauncherPanel.Views;

public partial class ServersPage : UserControl
{
    private LauncherConfig _config;
    private readonly GitHubService _github;

    public ServersPage(LauncherConfig config, GitHubService github = null)
    {
        InitializeComponent();
        _config = config;
        _github = github ?? new GitHubService();
        LoadSettings();
        RefreshGrid();
    }

    private void LoadSettings()
    {
        try
        {
            if (System.IO.File.Exists("panel-settings.json"))
            {
                var json = System.IO.File.ReadAllText("panel-settings.json");
                var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("token", out var token))
                    _github.SetToken(token.GetString() ?? "");
                if (doc.RootElement.TryGetProperty("owner", out var owner) && doc.RootElement.TryGetProperty("repo", out var repo))
                    _github.SetRepository(owner.GetString() ?? "mousrobys", repo.GetString() ?? "MaysLauncher");
            }
        }
        catch { }
    }

    public void UpdateConfig(LauncherConfig config)
    {
        _config = config;
        RefreshGrid();
    }

    public void SaveChanges()
    {
        if (ServersGrid.SelectedItem is SponsorServer item)
        {
            item.Name = TxtName.Text;
            item.Address = TxtAddress.Text;
            item.RequiredVersion = TxtVersion.Text;
            item.Description = TxtDescription.Text;
            item.Site = TxtSite.Text;
            item.Featured = ChkFeatured.IsChecked == true;
            ServersGrid.Items.Refresh();
        }
    }

    private void RefreshGrid()
    {
        ServersGrid.ItemsSource = null;
        ServersGrid.ItemsSource = _config.SponsorServers;
        ServersGrid.SelectionChanged += (s, e) =>
        {
            if (ServersGrid.SelectedItem is SponsorServer item)
            {
                TxtName.Text = item.Name;
                TxtAddress.Text = item.Address;
                TxtVersion.Text = item.RequiredVersion;
                TxtDescription.Text = item.Description;
                TxtSite.Text = item.Site;
                ChkFeatured.IsChecked = item.Featured;
            }
        };
    }

    private void BtnAddServer_Click(object sender, RoutedEventArgs e)
    {
        var server = new SponsorServer
        {
            Name = "Новый сервер",
            Address = "mc.example.com",
            RequiredVersion = "1.20.4",
            Description = "Описание...",
            Featured = true
        };
        _config.SponsorServers.Add(server);
        RefreshGrid();
    }

    private void BtnDeleteServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServersGrid.SelectedItem is SponsorServer item)
        {
            _config.SponsorServers.Remove(item);
            RefreshGrid();
        }
    }

    private async void BtnPublish_Click(object sender, RoutedEventArgs e)
    {
        SaveChanges();
        _github.SaveLocalConfig(_config);

        BtnPublish.IsEnabled = false;
        TxtPublishStatus.Text = "Публикация...";
        TxtPublishStatus.Foreground = System.Windows.Media.Brushes.White;

        var result = await _github.PushConfigAsync(_config, $"Обновление серверов: {DateTime.Now:yyyy-MM-dd HH:mm}");

        BtnPublish.IsEnabled = true;

        if (result)
        {
            TxtPublishStatus.Text = "✅ Серверы опубликованы! Обновление доступно в лаунчере.";
            TxtPublishStatus.Foreground = System.Windows.Media.Brushes.LimeGreen;
        }
        else
        {
            TxtPublishStatus.Text = "❌ Ошибка публикации. Проверьте токен в настройках.";
            TxtPublishStatus.Foreground = System.Windows.Media.Brushes.Red;
        }
    }
}
