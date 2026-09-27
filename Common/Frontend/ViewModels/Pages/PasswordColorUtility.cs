using Avalonia.Media;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

internal static class PasswordColorUtility
{
    public const string DefaultColor = "#FF14B8A6";

    public static string NormalizeKnownColor(string color) =>
        TryNormalizeHexColor(color, out var normalizedColor) ? normalizedColor : DefaultColor;

    public static bool TryNormalizeHexColor(string? input, out string normalizedColor)
    {
        normalizedColor = DefaultColor;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var hex = input.Trim();
        if (hex.StartsWith('#'))
            hex = hex[1..];

        hex = ExpandHexColor(hex);
        if (hex.Length != 8 || !hex.All(Uri.IsHexDigit))
            return false;

        normalizedColor = $"#{hex.ToUpperInvariant()}";
        return true;
    }

    public static double NormalizeComponent(double value) =>
        Math.Clamp(Math.Round(value), 0, 255);

    public static byte ToComponentByte(double value) =>
        (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    public static IBrush ParseBrush(string? color)
    {
        try
        {
            return Brush.Parse(string.IsNullOrWhiteSpace(color) ? DefaultColor : color);
        }
        catch
        {
            return Brush.Parse(DefaultColor);
        }
    }

    private static string ExpandHexColor(string hex) =>
        hex.Length switch
        {
            3 => $"FF{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}",
            4 => $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}{hex[3]}{hex[3]}",
            6 => $"FF{hex}",
            _ => hex
        };
}
