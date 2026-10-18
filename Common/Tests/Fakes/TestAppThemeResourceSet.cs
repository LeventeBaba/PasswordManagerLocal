using Avalonia.Controls;
using Avalonia.Media;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Theming;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class TestAppThemeResourceSet : IAppThemeResourceSet
{
    private readonly ResourceDictionary _resources = new();
    private int _disposed;

    public TestAppThemeResourceSet(
        AppThemeId themeId,
        IEnumerable<ThemeResourceKey>? resources = null)
    {
        ThemeId = themeId;
        foreach (var resource in resources ?? ThemeContract.RequiredResources)
        {
            _resources[ThemeContract.GetResourceName(resource)] =
                new SolidColorBrush(themeId == AppThemeId.Dark ? Colors.White : Colors.Black);
        }
    }

    public AppThemeId ThemeId { get; }

    public IResourceProvider Provider => _resources;

    public int DisposeCount { get; private set; }

    public bool TryGetResource(string name, out object? value) =>
        _resources.TryGetValue(name, out value);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        DisposeCount++;
        _resources.Clear();
    }
}
