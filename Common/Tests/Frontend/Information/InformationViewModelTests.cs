using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Information;
using PasswordManagerLocal.Common.Frontend.Localization;
using PasswordManagerLocal.Common.Frontend.ViewModels.Information;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Frontend.Information;

[TestClass]
public sealed class InformationViewModelTests
{
    [TestMethod]
    public async Task AboutUsesInjectedVersionAndRefreshesWhenLanguageChanges()
    {
        await using var preferences = await CreateLocalizedPreferencesAsync();
        using var viewModel = new AboutViewModel(
            preferences,
            "1.2.3",
            () => { },
            () => { },
            areLicensesAvailable: true);

        Assert.AreEqual("About", viewModel.Title);
        Assert.AreEqual("Version 1.2.3", viewModel.VersionText);
        Assert.IsTrue(viewModel.AreLicensesAvailable);

        var result = await preferences.SetLanguageAsync(AppLanguage.Hungarian);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Névjegy", viewModel.Title);
        Assert.AreEqual("Verzió: 1.2.3", viewModel.VersionText);
    }

    [TestMethod]
    public async Task LicenseListFiltersPackageNamesWithoutNetworkOrUserData()
    {
        await using var preferences = await CreateLocalizedPreferencesAsync();
        using var viewModel = new ThirdPartyLicensesViewModel(
            preferences,
            ThirdPartyNoticeCatalogLoader.Load(),
            () => { },
            _ => { });

        viewModel.SearchText = "ZXing.Net.Bindings.SkiaSharp";

        Assert.AreEqual(1, viewModel.FilteredItems.Count);
        Assert.AreEqual("ZXing.Net", viewModel.FilteredItems[0].Name);
        Assert.IsTrue(viewModel.HasResults);

        viewModel.SearchText = "not-a-real-package";

        Assert.IsTrue(viewModel.HasNoResults);
    }

    [TestMethod]
    public async Task LicenseDetailExposesCompleteOfflineNotice()
    {
        await using var preferences = await CreateLocalizedPreferencesAsync();
        using var viewModel = new ThirdPartyLicenseDetailViewModel(preferences, () => { });
        var notice = ThirdPartyNoticeCatalogLoader.Load().Notices
            .Single(item => item.Id == "sqlite3mc-bindings");

        viewModel.SetNotice(notice);

        Assert.AreEqual("SQLite3 Multiple Ciphers .NET bindings", viewModel.Title);
        Assert.AreEqual("MIT", viewModel.LicenseExpression);
        Assert.IsTrue(viewModel.LicenseText.Contains("Permission is hereby granted", StringComparison.Ordinal));
        Assert.IsTrue(viewModel.SourceUri?.Scheme == Uri.UriSchemeHttps);
    }

    private static Task<PasswordManagerLocal.Common.Frontend.Services.UiPreferencesService>
        CreateLocalizedPreferencesAsync() =>
        TestUiPreferencesFactory.CreateAsync(
            resourceLoader: new ShippedLocalizationLoader());

    private sealed class ShippedLocalizationLoader : IFrontendLocalizationResourceLoader
    {
        public ValueTask<Stream> OpenAsync(
            AppLanguage language,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = language == AppLanguage.Hungarian ? "hu.json" : "en_us.json";
            return ValueTask.FromResult<Stream>(File.OpenRead(
                Path.Combine(AppContext.BaseDirectory, "Localization", fileName)));
        }
    }
}
