using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PasswordManagerLocal.Common.Frontend.Theming;

internal sealed partial class FeatureControlThemes : ResourceDictionary
{
    public FeatureControlThemes() => AvaloniaXamlLoader.Load(this);
}
