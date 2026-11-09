using Avalonia.Controls;
using Avalonia.Media;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Theming;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal static class TestAppThemeManagerFactory
{
    public static AppThemeManager Create() =>
        new(
            AppThemeCatalog.CreateDefault(),
            new Loader(),
            new Dispatcher(),
            new Host());

    private sealed class Loader : IAppThemeResourceLoader
    {
        public ValueTask<IAppThemeResourceSet> LoadAsync(
            AppThemeDescriptor descriptor,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IAppThemeResourceSet>(new Resources(descriptor.Id));
        }
    }

    private sealed class Dispatcher : IAppThemeDispatcher
    {
        public Task InvokeAsync(Action operation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operation();
            return Task.CompletedTask;
        }
    }

    private sealed class Host : IAppThemeResourceHost
    {
        private readonly HashSet<Guid> _owners = [];
        private IAppThemeResourceSet? _active;

        public AppThemeId? ActiveThemeId => _active?.ThemeId;

        public void Register(Guid ownerId) => _owners.Add(ownerId);

        public void Replace(Guid ownerId, AppThemeDescriptor descriptor, IAppThemeResourceSet resources)
        {
            if (!_owners.Contains(ownerId))
                throw new InvalidOperationException("Unknown test theme owner.");
            var previous = _active;
            _active = resources;
            previous?.Dispose();
        }

        public void Release(Guid ownerId)
        {
            if (!_owners.Remove(ownerId) || _owners.Count != 0)
                return;
            var previous = _active;
            _active = null;
            previous?.Dispose();
        }
    }

    private sealed class Resources : IAppThemeResourceSet
    {
        private readonly ResourceDictionary _resources = new();

        public Resources(AppThemeId themeId)
        {
            ThemeId = themeId;
            foreach (var resource in ThemeContract.RequiredResources)
            {
                _resources[ThemeContract.GetResourceName(resource)] =
                    new SolidColorBrush(Colors.Black);
            }
        }

        public AppThemeId ThemeId { get; }

        public IResourceProvider Provider => _resources;

        public bool TryGetResource(string name, out object? value) =>
            _resources.TryGetValue(name, out value);

        public void Dispose() => _resources.Clear();
    }
}
