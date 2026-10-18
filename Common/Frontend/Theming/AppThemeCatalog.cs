namespace PasswordManagerLocal.Common.Frontend.Theming;

/// <summary>Immutable metadata catalog. Theme resource instances are never cached here.</summary>
public sealed class AppThemeCatalog
{
    private readonly IReadOnlyDictionary<AppThemeId, AppThemeDescriptor> _byId;

    public AppThemeCatalog(IEnumerable<AppThemeDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var ordered = descriptors.ToArray();
        if (ordered.Length == 0)
            throw new ArgumentException("At least one complete theme is required.", nameof(descriptors));
        if (ordered.Any(descriptor => descriptor is null ||
            !Enum.IsDefined(descriptor.Id) ||
            !Enum.IsDefined(descriptor.Brightness) ||
            string.IsNullOrWhiteSpace(descriptor.DisplayNameResourceKey)))
        {
            throw new ArgumentException("Every theme descriptor must be complete.", nameof(descriptors));
        }
        if (ordered.Select(descriptor => descriptor.Id).Distinct().Count() != ordered.Length)
            throw new ArgumentException("Theme identifiers must be unique.", nameof(descriptors));

        Descriptors = Array.AsReadOnly(ordered);
        _byId = ordered.ToDictionary(descriptor => descriptor.Id);
    }

    public IReadOnlyList<AppThemeDescriptor> Descriptors { get; }

    public bool TryGet(AppThemeId id, out AppThemeDescriptor descriptor)
    {
        if (_byId.TryGetValue(id, out var found))
        {
            descriptor = found;
            return true;
        }

        descriptor = null!;
        return false;
    }

    public AppThemeDescriptor GetRequired(AppThemeId id) =>
        TryGet(id, out var descriptor)
            ? descriptor
            : throw new ArgumentOutOfRangeException(nameof(id), id, null);

    public static AppThemeCatalog CreateDefault() =>
        new(
        [
            new AppThemeDescriptor(AppThemeId.Light, ThemeBrightness.Light, "Theme_Light"),
            new AppThemeDescriptor(AppThemeId.Dark, ThemeBrightness.Dark, "Theme_Dark")
        ]);
}
