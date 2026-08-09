using System.Windows;
using System.Windows.Controls;
using LauncherPanel.Models;

namespace LauncherPanel.Views;

public partial class NewsPage : UserControl
{
    private LauncherConfig _config;

    public NewsPage(LauncherConfig config)
    {
        InitializeComponent();
        _config = config;
        RefreshGrid();
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
}
