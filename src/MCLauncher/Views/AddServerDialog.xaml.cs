using System.Windows;
using System.Windows.Input;
using MCLauncher.Models;
using MCLauncher.Services;

namespace MCLauncher.Views;

public partial class AddServerDialog : Window
{
    public ServerEntry? Result { get; private set; }

    public AddServerDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => TxtName.Focus();
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var name = TxtName.Text.Trim();
        var address = TxtAddress.Text.Trim();
        var version = TxtVersion.Text.Trim();

        if (address.Length == 0)
        {
            TxtStatus.Text = "Укажите адрес сервера.";
            TxtAddress.Focus();
            return;
        }

        try
        {
            ServerPingService.ParseAddress(address);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = ex.Message;
            return;
        }

        if (name.Length == 0) name = address;
        if (version.Length == 0) version = "26.2";

        Result = new ServerEntry
        {
            Name = name,
            Address = address,
            RequiredVersion = version,
            Description = "Добавлен вручную",
            Featured = false,
            Loader = LoaderKind.Vanilla
        };

        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }
}
