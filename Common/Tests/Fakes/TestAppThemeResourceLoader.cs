using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Theming;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class TestAppThemeResourceLoader : IAppThemeResourceLoader
{
    public Func<AppThemeDescriptor, CancellationToken, ValueTask<IAppThemeResourceSet>>? Load { get; set; }

    public List<AppThemeId> Requests { get; } = [];

    public ValueTask<IAppThemeResourceSet> LoadAsync(
        AppThemeDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(descriptor.Id);
        return Load is null
            ? ValueTask.FromResult<IAppThemeResourceSet>(new TestAppThemeResourceSet(descriptor.Id))
            : Load(descriptor, cancellationToken);
    }
}
