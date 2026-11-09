namespace PasswordManagerLocal.Common.Frontend.Theming;

/// <summary>Owns the single theme dictionary currently attached to an application.</summary>
public interface IAppThemeResourceHost
{
    AppThemeId? ActiveThemeId { get; }

    void Register(Guid ownerId);

    void Replace(
        Guid ownerId,
        AppThemeDescriptor descriptor,
        IAppThemeResourceSet resources);

    void Release(Guid ownerId);
}
