using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MCLauncher.Services;

public static class ToastNotification
{
    private static Window? _owner;

    public static void Initialize(Window owner) => _owner = owner;

    public static void Show(string title, string message, NotificationType type = NotificationType.Info)
    {
        _owner?.Dispatcher.Invoke(() => ShowInternal(title, message, type));
    }

    private static void ShowInternal(string title, string message, NotificationType type)
    {
        var toast = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(22, 27, 34)),
            BorderBrush = new SolidColorBrush(GetColor(type)),
            BorderThickness = new Thickness(1, 1, 1, 3),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 8),
            MinWidth = 280,
            MaxWidth = 380,
            Opacity = 0,
            RenderTransform = new TranslateTransform(400, 0)
        };

        var panel = new StackPanel();
        var titleBlock = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(230, 237, 243))
        };
        var msgBlock = new TextBlock
        {
            Text = message,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(125, 133, 144)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };

        panel.Children.Add(titleBlock);
        if (!string.IsNullOrEmpty(message))
            panel.Children.Add(msgBlock);
        toast.Child = panel;

        var container = GetContainer();
        if (container == null)
        {
            var popup = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                Topmost = true,
                Width = 400,
                Height = 600,
                Left = SystemParameters.WorkArea.Width - 420,
                Top = SystemParameters.WorkArea.Height - 620
            };
            var sp = new StackPanel { Margin = new Thickness(16) };
            popup.Content = sp;
            popup.Show();
            _toastContainer = sp;
            container = sp;
        }

        container.Children.Insert(0, toast);

        var slideIn = new DoubleAnimation(0, 400, TimeSpan.FromMilliseconds(0));
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250));
        fadeIn.Completed += (_, _) =>
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
                fadeOut.Completed += (_, _) => container.Children.Remove(toast);
                toast.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            };
            timer.Start();
        };

        toast.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        toast.RenderTransform.BeginAnimation(TranslateTransform.XProperty, slideIn);
    }

    private static StackPanel? _toastContainer;
    private static StackPanel GetContainer() => _toastContainer;

    private static Color GetColor(NotificationType type) => type switch
    {
        NotificationType.Success => Color.FromRgb(35, 134, 54),
        NotificationType.Warning => Color.FromRgb(210, 153, 34),
        NotificationType.Error => Color.FromRgb(218, 54, 51),
        _ => Color.FromRgb(145, 70, 255)
    };
}

public enum NotificationType { Info, Success, Warning, Error }
