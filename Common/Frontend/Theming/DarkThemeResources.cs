using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PasswordManagerLocal.Common.Frontend.Theming;

public sealed partial class DarkThemeResources : ResourceDictionary
{
    public DarkThemeResources() => AvaloniaXamlLoader.Load(this);
}
