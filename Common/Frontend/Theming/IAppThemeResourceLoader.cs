namespace PasswordManagerLocal.Common.Frontend.Theming;

public interface IAppThemeResourceLoader
{
    ValueTask<IAppThemeResourceSet> LoadAsync(
        AppThemeDescriptor descriptor,
        CancellationToken cancellationToken = default);
}
