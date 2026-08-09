using System.Windows;
using System.Windows.Controls;
using LauncherPanel.Models;
using LauncherPanel.Services;

namespace LauncherPanel.Views;

public partial class NewsPage : UserControl
{
    private LauncherConfig _config;
    private readonly GitHubService _github;

    public NewsPage(LauncherConfig config, GitHubService github = null)
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
        if (NewsGrid.SelectedItem is NewsItem item)
        {
            item.Date = TxtDate.Text;
            item.Title = TxtTitle.Text;
            item.Content = TxtContent.Text;
            item.Important = ChkImportant.IsChecked == true;
            NewsGrid.Items.Refresh();
        }
    }

    private void RefreshGrid()
    {
        NewsGrid.ItemsSource = null;
        NewsGrid.ItemsSource = _config.News;
        NewsGrid.SelectionChanged += (s, e) =>
        {
            if (NewsGrid.SelectedItem is NewsItem item)
            {
                TxtDate.Text = item.Date;
                TxtTitle.Text = item.Title;
                TxtContent.Text = item.Content;
                ChkImportant.IsChecked = item.Important;
            }
        };
    }

    private void BtnAddNews_Click(object sender, RoutedEventArgs e)
    {
        var news = new NewsItem
        {
            Date = DateTime.Now.ToString("yyyy-MM-dd"),
            Title = "Новая новость",
            Content = "Текст новости..."
        };
        _config.News.Insert(0, news);
        RefreshGrid();
    }

    private void BtnDeleteNews_Click(object sender, RoutedEventArgs e)
    {
        if (NewsGrid.SelectedItem is NewsItem item)
        {
            _config.News.Remove(item);
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

        var result = await _github.PushConfigAsync(_config, $"Обновление новостей: {DateTime.Now:yyyy-MM-dd HH:mm}");

        BtnPublish.IsEnabled = true;

        if (result)
        {
            TxtPublishStatus.Text = "✅ Новость опубликована! Обновление доступно в лаунчере.";
            TxtPublishStatus.Foreground = System.Windows.Media.Brushes.LimeGreen;
        }
        else
        {
            TxtPublishStatus.Text = "❌ Ошибка публикации. Проверьте токен в настройках.";
            TxtPublishStatus.Foreground = System.Windows.Media.Brushes.Red;
        }
    }
}
