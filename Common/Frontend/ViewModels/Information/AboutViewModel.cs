using System.Globalization;
using PasswordManagerLocal.Common.Frontend.Services;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Information;

public sealed class AboutViewModel : ViewModelBase
{
    private readonly string? _applicationVersion;

    public AboutViewModel(
        UiPreferencesService uiPreferences,
        string? applicationVersion,
        Action navigateBack,
        Action navigateToLicenses,
        bool areLicensesAvailable)
        : base(uiPreferences)
    {
        ArgumentNullException.ThrowIfNull(navigateBack);
        ArgumentNullException.ThrowIfNull(navigateToLicenses);
        _applicationVersion = string.IsNullOrWhiteSpace(applicationVersion)
            ? null
            : applicationVersion.Trim();
        AreLicensesAvailable = areLicensesAvailable;
        NavigateBackCommand = Own(ReactiveCommand.Create(navigateBack));
        NavigateToLicensesCommand = Own(ReactiveCommand.Create(navigateToLicenses));
    }

    public ReactiveCommand<RxVoid, RxVoid> NavigateBackCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> NavigateToLicensesCommand { get; }
    public bool AreLicensesAvailable { get; }

    public string Title => GetTranslation("About_Title");
    public string Subtitle => GetTranslation("About_Subtitle");
    public string BackLabel => GetTranslation("Common_Back");
    public string ApplicationName => GetTranslation("AppTitle");
    public string ProductDescription => GetTranslation("About_ProductDescription");
    public string VersionText => _applicationVersion is null
        ? GetTranslation("About_Version_Unavailable")
        : string.Format(
            CultureInfo.CurrentCulture,
            GetTranslation("About_Version_Format"),
            _applicationVersion);
    public string LicensesTitle => GetTranslation("About_Licenses_Title");
    public string LicensesDescription => GetTranslation("About_Licenses_Description");

    protected override void OnLanguageChanged() =>
        RaisePropertiesChanged(
        [
            nameof(Title),
            nameof(Subtitle),
            nameof(BackLabel),
            nameof(ApplicationName),
            nameof(ProductDescription),
            nameof(VersionText),
            nameof(LicensesTitle),
            nameof(LicensesDescription)
        ]);
}
