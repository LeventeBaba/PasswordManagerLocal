using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Theming;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Frontend.Theming;

[TestClass]
public sealed class AppThemeManagerTests
{
    [TestMethod]
    public void CatalogAndShippedResourcesContainExactlyCompleteDarkAndLightThemes()
    {
        var catalog = AppThemeCatalog.CreateDefault();
        CollectionAssert.AreEqual(
            new[] { AppThemeId.Light, AppThemeId.Dark },
            catalog.Descriptors.Select(descriptor => descriptor.Id).ToArray());

        var required = ThemeContract.RequiredResources
            .Select(ThemeContract.GetResourceName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (var (theme, path) in new[]
        {
            (AppThemeId.Dark, ThemePath("Dark")),
            (AppThemeId.Light, ThemePath("Light"))
        })
        {
            var document = XDocument.Load(path);
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var entries = document.Root!.Elements().ToArray();
            Assert.IsTrue(entries.All(element => element.Name.LocalName == "SolidColorBrush"), theme.ToString());
            var actual = entries
                .Select(element => (string?)element.Attribute(x + "Key"))
                .Where(key => key is not null)
                .Cast<string>()
                .Order(StringComparer.Ordinal)
                .ToArray();
            CollectionAssert.AreEqual(required, actual, theme.ToString());
            Assert.AreEqual(actual.Length, actual.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [TestMethod]
    public async Task SuccessfulReplacementRunsOnDispatcherAndDisposesPriorResources()
    {
        var created = new List<TestAppThemeResourceSet>();
        var loader = new TestAppThemeResourceLoader
        {
            Load = (descriptor, _) =>
            {
                var resources = new TestAppThemeResourceSet(descriptor.Id);
                created.Add(resources);
                return ValueTask.FromResult<IAppThemeResourceSet>(resources);
            }
        };
        var dispatcher = new TestAppThemeDispatcher();
        var host = new TestAppThemeResourceHost();
        await using var manager = TestAppThemeFactory.CreateManager(loader, dispatcher, host);

        Assert.AreEqual(AppThemeChangeStatus.Applied, (await manager.SetThemeAsync(AppThemeId.Dark)).Status);
        Assert.AreEqual(AppThemeChangeStatus.Applied, (await manager.SetThemeAsync(AppThemeId.Light)).Status);

        Assert.AreEqual(AppThemeId.Light, manager.ActiveThemeId);
        Assert.AreEqual(AppThemeId.Light, host.ActiveThemeId);
        Assert.AreEqual(2, dispatcher.InvocationCount);
        Assert.AreEqual(2, host.ReplacementCount);
        Assert.AreEqual(1, created[0].DisposeCount);
        Assert.AreEqual(0, created[1].DisposeCount);
    }

    [TestMethod]
    public async Task InvalidCandidateIsDisposedAndPreservesCommittedTheme()
    {
        TestAppThemeResourceSet? invalid = null;
        var loader = new TestAppThemeResourceLoader
        {
            Load = (descriptor, _) =>
            {
                if (descriptor.Id == AppThemeId.Dark)
                    return ValueTask.FromResult<IAppThemeResourceSet>(new TestAppThemeResourceSet(descriptor.Id));
                invalid = new TestAppThemeResourceSet(
                    descriptor.Id,
                    ThemeContract.RequiredResources.Where(resource =>
                        resource != ThemeResourceKey.DialogOverlay));
                return ValueTask.FromResult<IAppThemeResourceSet>(invalid);
            }
        };
        var host = new TestAppThemeResourceHost();
        await using var manager = TestAppThemeFactory.CreateManager(loader, host: host);
        await manager.SetThemeAsync(AppThemeId.Dark);
        var active = host.ActiveResources;

        var result = await manager.SetThemeAsync(AppThemeId.Light);

        Assert.AreEqual(AppThemeChangeStatus.Failed, result.Status);
        Assert.AreEqual(AppThemeId.Dark, manager.ActiveThemeId);
        Assert.AreSame(active, host.ActiveResources);
        Assert.AreEqual(1, host.ReplacementCount);
        Assert.AreEqual(1, invalid!.DisposeCount);
    }

    [TestMethod]
    public async Task NewestRequestCancelsOlderLoadAndKeepsLatestSelection()
    {
        var olderEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var neverRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new TestAppThemeResourceLoader
        {
            Load = async (descriptor, cancellationToken) =>
            {
                if (descriptor.Id == AppThemeId.Light)
                {
                    olderEntered.TrySetResult();
                    await neverRelease.Task.WaitAsync(cancellationToken);
                }
                return new TestAppThemeResourceSet(descriptor.Id);
            }
        };
        var host = new TestAppThemeResourceHost();
        await using var manager = TestAppThemeFactory.CreateManager(loader, host: host);
        await manager.SetThemeAsync(AppThemeId.Dark);

        var older = manager.SetThemeAsync(AppThemeId.Light);
        await olderEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var newer = manager.SetThemeAsync(AppThemeId.Dark);

        Assert.AreEqual(AppThemeChangeStatus.Unchanged, (await newer).Status);
        Assert.AreEqual(AppThemeChangeStatus.Superseded, (await older).Status);
        Assert.AreEqual(AppThemeId.Dark, host.ActiveThemeId);
        Assert.AreEqual(1, host.ReplacementCount);
    }

    [TestMethod]
    public async Task CallerCancellationPreservesCommittedTheme()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var neverRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new TestAppThemeResourceLoader
        {
            Load = async (descriptor, cancellationToken) =>
            {
                if (descriptor.Id == AppThemeId.Light)
                {
                    entered.TrySetResult();
                    await neverRelease.Task.WaitAsync(cancellationToken);
                }
                return new TestAppThemeResourceSet(descriptor.Id);
            }
        };
        var host = new TestAppThemeResourceHost();
        await using var manager = TestAppThemeFactory.CreateManager(loader, host: host);
        await manager.SetThemeAsync(AppThemeId.Dark);
        using var cancellation = new CancellationTokenSource();

        var pending = manager.SetThemeAsync(AppThemeId.Light, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        Assert.AreEqual(AppThemeChangeStatus.Canceled, (await pending).Status);
        Assert.AreEqual(AppThemeId.Dark, host.ActiveThemeId);
        Assert.AreEqual(1, host.ReplacementCount);
    }

    [TestMethod]
    public async Task AndroidStyleOverlappingCloseAndReopenKeepsOnlyNewestSessionTheme()
    {
        var host = new TestAppThemeResourceHost();
        var first = TestAppThemeFactory.CreateManager(host: host);
        await first.SetThemeAsync(AppThemeId.Dark);
        var firstResources = (TestAppThemeResourceSet)host.ActiveResources!;
        var reopened = TestAppThemeFactory.CreateManager(host: host);
        await reopened.SetThemeAsync(AppThemeId.Light);
        var reopenedResources = (TestAppThemeResourceSet)host.ActiveResources!;

        Assert.AreEqual(1, firstResources.DisposeCount);
        Assert.AreEqual(AppThemeId.Light, host.ActiveThemeId);
        await reopened.DisposeAsync();
        Assert.AreSame(reopenedResources, host.ActiveResources);
        Assert.AreEqual(AppThemeId.Light, host.ActiveThemeId);
        Assert.AreEqual(0, reopenedResources.DisposeCount);

        await first.DisposeAsync();
        Assert.IsNull(host.ActiveResources);
        Assert.AreEqual(1, reopenedResources.DisposeCount);

        var third = TestAppThemeFactory.CreateManager(host: host);
        await third.SetThemeAsync(AppThemeId.Dark);
        Assert.AreEqual(AppThemeId.Dark, host.ActiveThemeId);
        await third.DisposeAsync();
        Assert.IsNull(host.ActiveResources);
    }

    [TestMethod]
    public void ThemeRuntimeHasNoProcessStaticResourceCache()
    {
        foreach (var type in new[]
        {
            typeof(AppThemeManager),
            typeof(AppThemeCatalog),
            typeof(AvaloniaAppThemeResourceLoader),
            typeof(AvaloniaAppThemeResourceHost)
        })
        {
            Assert.AreEqual(
                0,
                type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Count(field => !field.IsLiteral),
                type.FullName);
        }
    }

    private static string ThemePath(string theme, [CallerFilePath] string sourcePath = "") =>
        Path.Combine(
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "..", "..")),
            "Frontend",
            "Assets",
            "Themes",
            theme,
            "Theme.axaml");
}
