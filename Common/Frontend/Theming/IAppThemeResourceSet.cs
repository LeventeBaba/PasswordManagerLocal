using Avalonia.Controls;

namespace PasswordManagerLocal.Common.Frontend.Theming;

public interface IAppThemeResourceSet : IDisposable
{
    AppThemeId ThemeId { get; }

    IResourceProvider Provider { get; }

    bool TryGetResource(string name, out object? value);
}
