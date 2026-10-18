namespace PasswordManagerLocal.Common.Frontend.Theming;

/// <summary>Fallback owner for design-time or non-application service construction.</summary>
internal sealed class DetachedAppThemeResourceHost : IAppThemeResourceHost
{
    private readonly HashSet<Guid> _owners = [];
    private IAppThemeResourceSet? _activeResources;

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

        var previous = _activeResources;
        _activeResources = resources;
        previous?.Dispose();
    }

    public void Release(Guid ownerId)
    {
        if (!_owners.Remove(ownerId) || _owners.Count != 0)
            return;
        var previous = _activeResources;
        _activeResources = null;
        previous?.Dispose();
    }
}
