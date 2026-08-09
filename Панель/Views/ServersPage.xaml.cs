using System.Windows;
using System.Windows.Controls;
using LauncherPanel.Models;

namespace LauncherPanel.Views;

public partial class ServersPage : UserControl
{
    private LauncherConfig _config;

    public ServersPage(LauncherConfig config)
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
}
