using System.Windows;
using System.Windows.Controls;
using LauncherPanel.Services;
using Microsoft.Win32;

namespace LauncherPanel.Views;

public partial class ReleasePage : UserControl
{
    private readonly GitHubService _github;

    public ReleasePage(GitHubService github)
    {
        InitializeComponent();
        _github = github;
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Executable files (*.exe)|*.exe",
            Title = "Выберите EXE файл лаунчера"
        };

        if (dialog.ShowDialog() == true)
        {
            TxtExePath.Text = dialog.FileName;
        }
    }

    private async void BtnCreateRelease_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtTag.Text))
        {
            MessageBox.Show("Укажите тег версии!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BtnCreateRelease.IsEnabled = false;
        TxtReleaseStatus.Text = "Создание релиза...";

        string? exePath = null;
        if (!string.IsNullOrWhiteSpace(TxtExePath.Text) && System.IO.File.Exists(TxtExePath.Text))
        {
            exePath = TxtExePath.Text;
        }

        var result = await _github.CreateReleaseAsync(TxtTag.Text, TxtName.Text, TxtBody.Text, exePath);

        if (result)
        {
            TxtReleaseStatus.Text = $"✅ Релиз {TxtTag.Text} успешно создан!";
            TxtReleaseStatus.Foreground = System.Windows.Media.Brushes.LimeGreen;
        }
        else
        {
            TxtReleaseStatus.Text = "❌ Ошибка при создании релиза";
            TxtReleaseStatus.Foreground = System.Windows.Media.Brushes.Red;
        }

        BtnCreateRelease.IsEnabled = true;
    }

    private async void RefreshReleases_Click(object sender, RoutedEventArgs e)
    {
        TxtReleases.Text = "Загрузка...";
        var releases = await _github.GetReleasesAsync();
        TxtReleases.Text = releases ?? "Не удалось загрузить релизы";
    }
}
