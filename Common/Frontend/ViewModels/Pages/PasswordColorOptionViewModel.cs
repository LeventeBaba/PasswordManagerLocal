using Avalonia.Media;
using ReactiveUI;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

public sealed class PasswordColorOptionViewModel : ReactiveObject
{
    private string _displayName;
    private string _hexValue;
    private IBrush _brush;

    public PasswordColorOptionViewModel(string key, string displayName, string hexValue, bool isManageColorsOption = false)
    {
        Key = key;
        IsManageColorsOption = isManageColorsOption;
        IsRegularColorOption = !isManageColorsOption;
        _displayName = displayName;
        _hexValue = hexValue;
        _brush = ParseBrush(hexValue);
    }

    public string Key { get; }

    public bool IsManageColorsOption { get; }

    public bool IsRegularColorOption { get; }

    public string DisplayName
    {
        get => _displayName;
        private set => this.RaiseAndSetIfChanged(ref _displayName, value);
    }

    public string HexValue
    {
        get => _hexValue;
        private set => this.RaiseAndSetIfChanged(ref _hexValue, value);
    }

    public IBrush Brush
    {
        get => _brush;
        private set => this.RaiseAndSetIfChanged(ref _brush, value);
    }

    public void Update(string displayName, string hexValue)
    {
        DisplayName = displayName;
        HexValue = hexValue;
        Brush = ParseBrush(hexValue);
    }

    private static IBrush ParseBrush(string color)
    {
        try
        {
            return global::Avalonia.Media.Brush.Parse(color);
        }
        catch
        {
            return global::Avalonia.Media.Brush.Parse(PasswordColorUtility.DefaultColor);
        }
    }
}
