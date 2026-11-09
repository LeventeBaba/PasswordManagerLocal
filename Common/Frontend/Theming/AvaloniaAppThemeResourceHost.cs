using Avalonia;
using Avalonia.Styling;

namespace PasswordManagerLocal.Common.Frontend.Theming;

/// <summary>
/// Stores one selected theme in an Application instance. This is an active resource
/// owner, not a cache; replacement removes and disposes the prior dictionary.
/// </summary>
public sealed class AvaloniaAppThemeResourceHost : IAppThemeResourceHost
{
    private readonly Application _application;
    private readonly HashSet<Guid> _owners = [];
    private IAppThemeResourceSet? _activeResources;

    public AvaloniaAppThemeResourceHost(Application application) =>
        _application = application ?? throw new ArgumentNullException(nameof(application));

    public AppThemeId? ActiveThemeId => _activeResources?.ThemeId;

    public void Register(Guid ownerId)
    {
        if (ownerId == Guid.Empty || !_owners.Add(ownerId))
            throw new InvalidOperationException("The theme resource owner is already registered or invalid.");
    }

    public void Replace(
        Guid ownerId,
        AppThemeDescriptor descriptor,
        IAppThemeResourceSet resources)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(resources);
        if (!_owners.Contains(ownerId))
            throw new InvalidOperationException("The theme resource owner is not registered.");
        if (resources.ThemeId != descriptor.Id)
            throw new InvalidDataException("The resource set does not match its theme descriptor.");

        var merged = _application.Resources.MergedDictionaries;
        var previousResources = _activeResources;
        var previousVariant = _application.RequestedThemeVariant;
        var added = false;
        try
        {
            merged.Add(resources.Provider);
            added = true;
            _application.RequestedThemeVariant = descriptor.Brightness == ThemeBrightness.Light
                ? ThemeVariant.Light
                : ThemeVariant.Dark;
            if (previousResources is not null)
                merged.Remove(previousResources.Provider);
            _activeResources = resources;
        }
        catch
        {
            if (added)
                merged.Remove(resources.Provider);
            _application.RequestedThemeVariant = previousVariant;
            _activeResources = previousResources;
            throw;
        }

        previousResources?.Dispose();
    }

    public void Release(Guid ownerId)
    {
        if (!_owners.Remove(ownerId) || _owners.Count != 0 ||
            _activeResources is not { } resources)
            return;

        _application.Resources.MergedDictionaries.Remove(resources.Provider);
        _activeResources = null;
        _application.RequestedThemeVariant = ThemeVariant.Default;
        resources.Dispose();
    }
}
