using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MCLauncher;

/// <summary>Прячет чип «наиграно», если в сборке ещё не было игровых сессий.</summary>
public sealed class PlayTimeToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var seconds = value is int i ? i : 0;
        return seconds >= 60 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
