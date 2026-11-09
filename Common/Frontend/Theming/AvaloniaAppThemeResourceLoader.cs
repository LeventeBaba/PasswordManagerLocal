using Avalonia.Controls;

namespace PasswordManagerLocal.Common.Frontend.Theming;

/// <summary>Creates only the requested compiled theme resource dictionary.</summary>
public sealed class AvaloniaAppThemeResourceLoader : IAppThemeResourceLoader
{
    private readonly IReadOnlyDictionary<AppThemeId, Func<IResourceDictionary>> _factories;

    public AvaloniaAppThemeResourceLoader()
    {
        _factories = new Dictionary<AppThemeId, Func<IResourceDictionary>>
        {
            [AppThemeId.Dark] = static () => new DarkThemeResources(),
            [AppThemeId.Light] = static () => new LightThemeResources()
        };
    }

    public ValueTask<IAppThemeResourceSet> LoadAsync(
        AppThemeDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_factories.TryGetValue(descriptor.Id, out var factory))
            throw new FileNotFoundException("The selected frontend theme resource is unavailable.");

        IAppThemeResourceSet resources = new AvaloniaAppThemeResourceSet(
            descriptor.Id,
            factory());
        if (cancellationToken.IsCancellationRequested)
        {
            resources.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return ValueTask.FromResult(resources);
    }
}
