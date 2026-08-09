using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace LauncherPanel;

public class NewsItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString().Substring(0, 8);
    public string Date { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public bool Important { get; set; }
}

public class ServerItem
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string RequiredVersion { get; set; } = "";
    public string Description { get; set; } = "";
    public string Site { get; set; } = "";
    public bool Featured { get; set; } = true;
}

public class ConfigData
{
    public List<NewsItem> News { get; set; } = new();
    public List<ServerItem> SponsorServers { get; set; } = new();
}

public class BoolToMarkConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (bool)value ? "✓" : "✗";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public partial class MainWindow : Window
{
    private const string SettingsFile = "panel-settings.json";
    private ConfigData _config = new();
    private string _owner = "mousrobys";
    private string _repo = "MaysLauncher";
    private string _token = "";

    public MainWindow()
    {
        InitializeComponent();
        Resources.Add("BoolToMarkConverter", new BoolToMarkConverter());
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        LoadConfig();
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("owner", out var o)) _owner = o.GetString() ?? _owner;
                if (doc.RootElement.TryGetProperty("repo", out var r)) _repo = r.GetString() ?? _repo;
                if (doc.RootElement.TryGetProperty("token", out var t)) _token = t.GetString() ?? "";
            }
        }
        catch { }

        TxtOwner.Text = _owner;
        TxtRepo.Text = _repo;
        TxtToken.Text = _token;
    }

    private void LoadConfig()
    {
        try
        {
            var configPath = Path.Combine(GetBasePath(), "launcher-config.json");
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                _config = JsonSerializer.Deserialize<ConfigData>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ConfigData();
            }
        }
        catch { }

        RefreshGrids();
    }

    private void RefreshGrids()
    {
        NewsGrid.ItemsSource = null;
        NewsGrid.ItemsSource = _config.News;
        ServersGrid.ItemsSource = null;
        ServersGrid.ItemsSource = _config.SponsorServers;
    }

    private string GetBasePath()
    {
        return Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory)?.FullName ?? AppDomain.CurrentDomain.BaseDirectory;
    }

    private void BtnCreateNews_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NewsDialog();
        if (dialog.ShowDialog() == true)
        {
            var news = dialog.News;
            _config.News.Insert(0, news);
            RefreshGrids();

            if (dialog.ShouldPublish)
                SaveAndPublish(news);
            else
                SaveLocal(news);
        }
    }

    private void SaveLocal(NewsItem news)
    {
        try
        {
            var configPath = Path.Combine(GetBasePath(), "launcher-config.json");
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
            SetStatus("Новость сохранена локально");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка: " + ex.Message);
        }
    }

    private async void SaveAndPublish(NewsItem news)
    {
        try
        {
            var configPath = Path.Combine(GetBasePath(), "launcher-config.json");
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
            SetStatus("Новость сохранена локально");

            await PublishToGitHub(news.Title);
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка: " + ex.Message);
        }
    }

    private async Task PublishToGitHub(string title)
    {
        try
        {
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

            string sha = null;
            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Add("User-Agent", "LauncherPanel");
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);

                var getUrl = $"https://api.github.com/repos/{_owner}/{_repo}/contents/launcher-config.json";
                var getResponse = await http.GetAsync(getUrl);
                if (getResponse.IsSuccessStatusCode)
                {
                    var content = await getResponse.Content.ReadAsStringAsync();
                    var doc = JsonDocument.Parse(content);
                    sha = doc.RootElement.TryGetProperty("sha", out var shaElem) ? shaElem.GetString() : null;
                }
            }
            catch { }

            using var http2 = new HttpClient();
            http2.DefaultRequestHeaders.Add("User-Agent", "LauncherPanel");
            http2.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
            http2.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

            var body = new { message = $"News: {title} ({DateTime.Now:yyyy-MM-dd HH:mm})", content = base64, sha };
            var bodyJson = JsonSerializer.Serialize(body);
            var content_req = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            var url = $"https://api.github.com/repos/{_owner}/{_repo}/contents/launcher-config.json";
            var response = await http2.PutAsync(url, content_req);

            if (response.IsSuccessStatusCode)
            {
                SetStatus("✓ Новость опубликована на GitHub!");
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                SetStatus("Ошибка GitHub: " + response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка публикации: " + ex.Message);
        }
    }

    private void BtnAddServer_Click(object sender, RoutedEventArgs e)
    {
        _config.SponsorServers.Add(new ServerItem { Name = "Сервер", Address = "mc.example.com" });
        RefreshGrids();
    }

    private async void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
    {
        _owner = TxtOwner.Text.Trim();
        _repo = TxtRepo.Text.Trim();
        _token = TxtToken.Text.Trim();

        try
        {
            var settings = JsonSerializer.Serialize(new { owner = _owner, repo = _repo, token = _token });
            File.WriteAllText(SettingsFile, settings);
            SetStatus("Настройки сохранены");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка: " + ex.Message);
        }
    }

    private async void BtnTestConnection_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as Button;
        btn!.IsEnabled = false;
        SetStatus("Проверка...");

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "LauncherPanel");
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);

            var url = $"https://api.github.com/repos/{_owner}/{_repo}";
            var response = await http.GetAsync(url);

            SetStatus(response.IsSuccessStatusCode ? "Подключение успешно!" : "Ошибка подключения");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка: " + ex.Message);
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    private void SetStatus(string text)
    {
        Dispatcher.Invoke(() => TxtStatus.Text = text);
    }
}
