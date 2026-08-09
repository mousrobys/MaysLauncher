using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

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
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        return Directory.GetParent(dir)?.FullName ?? dir;
    }

    private async void BtnPublish_Click(object sender, RoutedEventArgs e)
    {
        BtnPublish.IsEnabled = false;
        SetStatus("Публикация...");

        try
        {
            var success = await PublishConfigAsync();
            SetStatus(success ? "Опубликовано!" : "Ошибка публикации");
        }
        catch (Exception ex)
        {
            SetStatus($"Ошибка: {ex.Message}");
        }
        finally
        {
            BtnPublish.IsEnabled = true;
        }
    }

    private async Task<bool> PublishConfigAsync()
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
                    sha = doc.RootElement.GetProperty("content").GetString();
                }
            }
            catch { }

            using var http2 = new HttpClient();
            http2.DefaultRequestHeaders.Add("User-Agent", "LauncherPanel");
            http2.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
            http2.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

            var body = new { message = $"Update config: {DateTime.Now:yyyy-MM-dd HH:mm}", content = base64, sha = sha };
            var bodyJson = JsonSerializer.Serialize(body);
            var content_req = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            var url = $"https://api.github.com/repos/{_owner}/{_repo}/contents/launcher-config.json";
            var response = await http2.PutAsync(url, content_req);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var configPath = Path.Combine(GetBasePath(), "launcher-config.json");
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
            SetStatus("Сохранено локально");
        }
        catch (Exception ex)
        {
            SetStatus($"Ошибка: {ex.Message}");
        }
    }

    private void BtnAddNews_Click(object sender, RoutedEventArgs e)
    {
        _config.News.Insert(0, new NewsItem { Title = "Новая новость", Content = "Текст..." });
        RefreshGrids();
    }

    private void BtnDeleteNews_Click(object sender, RoutedEventArgs e)
    {
        if (NewsGrid.SelectedItem is NewsItem item)
        {
            _config.News.Remove(item);
            RefreshGrids();
        }
    }

    private void BtnAddServer_Click(object sender, RoutedEventArgs e)
    {
        _config.SponsorServers.Add(new ServerItem { Name = "Сервер", Address = "mc.example.com" });
        RefreshGrids();
    }

    private void BtnDeleteServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServersGrid.SelectedItem is ServerItem item)
        {
            _config.SponsorServers.Remove(item);
            RefreshGrids();
        }
    }

    private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
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
            SetStatus($"Ошибка: {ex.Message}");
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
            SetStatus($"Ошибка: {ex.Message}");
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
