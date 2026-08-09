using System.Windows;

namespace LauncherPanel;

public partial class NewsDialog : Window
{
    public NewsItem News { get; private set; }
    public bool ShouldPublish { get; private set; }

    public NewsDialog()
    {
        InitializeComponent();
        News = new NewsItem();
    }

    private void FillNews()
    {
        if (string.IsNullOrWhiteSpace(TxtTitle.Text))
        {
            MessageBox.Show("Введите заголовок", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        News.Title = TxtTitle.Text.Trim();
        News.Content = TxtContent.Text.Trim();
        News.Important = ChkImportant.IsChecked == true;
        News.Date = DateTime.Now.ToString("yyyy-MM-dd");

        DialogResult = true;
        Close();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        ShouldPublish = false;
        FillNews();
    }

    private void BtnPublish_Click(object sender, RoutedEventArgs e)
    {
        ShouldPublish = true;
        FillNews();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
