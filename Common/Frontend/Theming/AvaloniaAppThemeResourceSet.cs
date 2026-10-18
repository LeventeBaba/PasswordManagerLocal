using Avalonia.Controls;
using Avalonia.Styling;

namespace PasswordManagerLocal.Common.Frontend.Theming;

internal sealed class AvaloniaAppThemeResourceSet : IAppThemeResourceSet
{
    private readonly IResourceDictionary _resources;
    private int _disposed;

    public AvaloniaAppThemeResourceSet(AppThemeId themeId, IResourceDictionary resources)
    {
        ThemeId = themeId;
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
    }

    public AppThemeId ThemeId { get; }

    public IResourceProvider Provider => _resources;

    public bool TryGetResource(string name, out object? value)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            value = null;
            return false;
        }

        return _resources.TryGetResource(name, ThemeVariant.Default, out value);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _resources.ThemeDictionaries.Clear();
        _resources.MergedDictionaries.Clear();
        _resources.Clear();
    }
}
