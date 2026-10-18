using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Theming;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class TestAppThemeResourceHost : IAppThemeResourceHost
{
    private readonly HashSet<Guid> _owners = [];

    public AppThemeId? ActiveThemeId => ActiveResources?.ThemeId;

    public IAppThemeResourceSet? ActiveResources { get; private set; }

    public int ReplacementCount { get; private set; }

    public int ReleaseCount { get; private set; }

    public Exception? ReplaceFailure { get; set; }

    public void Register(Guid ownerId)
    {
        if (!_owners.Add(ownerId))
            throw new InvalidOperationException("Duplicate test theme owner.");
    }

    public void Replace(
        Guid ownerId,
        AppThemeDescriptor descriptor,
        IAppThemeResourceSet resources)
    {
        if (!_owners.Contains(ownerId))
            throw new InvalidOperationException("Unknown test theme owner.");
        if (ReplaceFailure is not null)
            throw ReplaceFailure;
        if (descriptor.Id != resources.ThemeId)
            throw new InvalidDataException("The test resource set does not match its descriptor.");
        var previous = ActiveResources;
        ActiveResources = resources;
        ReplacementCount++;
        previous?.Dispose();
    }

    public void Release(Guid ownerId)
    {
        if (!_owners.Remove(ownerId) || _owners.Count != 0 || ActiveResources is null)
            return;
        var previous = ActiveResources;
        ActiveResources = null;
        ReleaseCount++;
        previous.Dispose();
    }
}
