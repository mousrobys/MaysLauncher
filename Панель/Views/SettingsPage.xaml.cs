using System.Windows;
using System.Windows.Controls;
using LauncherPanel.Services;
using Microsoft.Win32;

namespace LauncherPanel.Views;

public partial class SettingsPage : UserControl
{
    private readonly GitHubService _github;
    private readonly Action<string, string> _onRepoChanged;

    public event Action<string>? TokenSet;

    public SettingsPage(GitHubService github, Action<string, string> onRepoChanged)
    {
        InitializeComponent();
        _github = github;
        _onRepoChanged = onRepoChanged;

        TxtOwner.Text = _github.Owner;
        TxtRepo.Text = _github.Repo;

        LoadSettings();
    }

    private void LoadSettings()
    {
        var settingsPath = "panel-settings.json";
        if (System.IO.File.Exists(settingsPath))
        {
            try
            {
                var json = System.IO.File.ReadAllText(settingsPath);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("token", out var token))
                    TxtToken.Password = token.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("defaultExe", out var exe))
                    TxtDefaultExe.Text = exe.GetString() ?? "билды exe/MaysLauncher.exe";
            }
            catch { }
        }
    }

    private void SaveSettings()
    {
        var settings = new System.Text.Json.Nodes.JsonObject
        {
            ["owner"] = TxtOwner.Text,
            ["repo"] = TxtRepo.Text,
            ["token"] = TxtToken.Password,
            ["defaultExe"] = TxtDefaultExe.Text
        };

        var path = "panel-settings.json";
        System.IO.File.WriteAllText(path, settings.ToJsonString());
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        _github.SetRepository(TxtOwner.Text, TxtRepo.Text);
        _onRepoChanged(TxtOwner.Text, TxtRepo.Text);

        if (!string.IsNullOrEmpty(TxtToken.Password))
        {
            _github.SetToken(TxtToken.Password);
            TokenSet?.Invoke(TxtToken.Password);
        }

        SaveSettings();
        TxtSettingsStatus.Text = "✅ Настройки сохранены";
    }

    private async void BtnTest_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtToken.Password))
            _github.SetToken(TxtToken.Password);
        _github.SetRepository(TxtOwner.Text, TxtRepo.Text);

        TxtSettingsStatus.Text = "Проверка подключения...";
        BtnTest.IsEnabled = false;

        var result = await _github.TestConnectionAsync();

        BtnTest.IsEnabled = true;
        TxtSettingsStatus.Text = result
            ? "✅ Подключение успешно!"
            : "❌ Не удалось подключиться. Проверьте токен и репозиторий.";
    }

    private void BrowseDefaultExe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Executable files (*.exe)|*.exe",
            Title = "Выберите EXE по умолчанию"
        };

        if (dialog.ShowDialog() == true)
        {
            TxtDefaultExe.Text = dialog.FileName;
        }
    }
}
