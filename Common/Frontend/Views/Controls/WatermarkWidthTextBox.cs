using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PasswordManagerLocal.Common.Frontend.Views.Controls;

public sealed class WatermarkWidthTextBox : TextBox
{
    private const double ExtraHorizontalBuffer = 8;

    protected override Type StyleKeyOverride => typeof(TextBox);

    protected override Size MeasureOverride(Size availableSize)
    {
        var desiredSize = base.MeasureOverride(availableSize);
        var watermarkWidth = MeasureWatermarkDesiredWidth();
        var desiredWidth = Math.Max(desiredSize.Width, watermarkWidth);

        if (!double.IsInfinity(availableSize.Width))
        {
            desiredWidth = Math.Min(desiredWidth, availableSize.Width);
        }

        return new Size(desiredWidth, desiredSize.Height);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == PlaceholderTextProperty
            || change.Property == FontFamilyProperty
            || change.Property == FontSizeProperty
            || change.Property == FontStyleProperty
            || change.Property == FontWeightProperty
            || change.Property == FontStretchProperty
            || change.Property == PaddingProperty)
        {
            InvalidateMeasure();
        }
    }

    private double MeasureWatermarkDesiredWidth()
    {
        if (string.IsNullOrWhiteSpace(PlaceholderText))
        {
            return 0;
        }

        var formattedText = new FormattedText(
            PlaceholderText,
            CultureInfo.CurrentUICulture,
            FlowDirection,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
            FontSize,
            Foreground ?? Brushes.Black);

        return Math.Ceiling(
            formattedText.WidthIncludingTrailingWhitespace
            + Padding.Left
            + Padding.Right
            + ExtraHorizontalBuffer);
    }
}
