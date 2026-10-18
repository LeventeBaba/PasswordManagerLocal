using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

namespace PasswordManagerLocal.Common.Frontend.Views.Converters;

/// <summary>Converts stored user color data to a brush at the view boundary.</summary>
public sealed class UserColorBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = value is string candidate && !string.IsNullOrWhiteSpace(candidate)
            ? candidate
            : PasswordColorUtility.DefaultColor;

        try
        {
            return Brush.Parse(color);
        }
        catch
        {
            return Brush.Parse(PasswordColorUtility.DefaultColor);
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("User color brush conversion is one-way.");
}
