using System.Windows;
using System.Windows.Input;

namespace MCLauncher.Views;

public partial class TextInputDialog : Window
{
    public string Value => TxtValue.Text;

    public TextInputDialog(string title, string prompt, string placeholder = "")
    {
        InitializeComponent();

        Title = title;
        TxtTitle.Text = title;
        TxtPrompt.Text = prompt;
        TxtValue.Text = "";
        TxtValue.ToolTip = placeholder;

        Loaded += (_, _) => TxtValue.Focus();
    }

    private void TxtValue_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) BtnOk_Click(sender, e);
        else if (e.Key == Key.Escape) BtnCancel_Click(sender, e);
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
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
