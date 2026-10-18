using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PasswordManagerLocal.Common.Frontend.Theming;

public sealed partial class LightThemeResources : ResourceDictionary
{
    public LightThemeResources() => AvaloniaXamlLoader.Load(this);
}
