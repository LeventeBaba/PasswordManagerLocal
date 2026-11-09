using PasswordManagerLocal.Common.Frontend.Information;
using PasswordManagerLocal.Common.Frontend.Services;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Information;

public sealed class ThirdPartyLicenseDetailViewModel : ViewModelBase
{
    private readonly Action _navigateBack;
    private ThirdPartyNotice? _notice;

    public ThirdPartyLicenseDetailViewModel(
        UiPreferencesService uiPreferences,
        Action navigateBack)
        : base(uiPreferences)
    {
        _navigateBack = navigateBack ?? throw new ArgumentNullException(nameof(navigateBack));
        NavigateBackCommand = Own(ReactiveCommand.Create(_navigateBack));
    }

    public ReactiveCommand<RxVoid, RxVoid> NavigateBackCommand { get; }
    public string Title => _notice?.Name ?? GetTranslation("Licenses_Detail_Title");
    public string Subtitle => GetTranslation("Licenses_Detail_Subtitle");
    public string BackLabel => GetTranslation("Common_Back");
    public string VersionLabel => GetTranslation("Licenses_Version_Label");
    public string LicenseLabel => GetTranslation("Licenses_License_Label");
    public string PlatformsLabel => GetTranslation("Licenses_Platforms_Label");
    public string PackagesLabel => GetTranslation("Licenses_Packages_Label");
    public string CopyrightLabel => GetTranslation("Licenses_Copyright_Label");
    public string RequiredNoticeLabel => GetTranslation("Licenses_RequiredNotice_Label");
    public string FullLicenseLabel => GetTranslation("Licenses_FullText_Label");
    public string OpenSourcePageLabel => GetTranslation("Licenses_OpenSourcePage");
    public string OpenPackagePageLabel => GetTranslation("Licenses_OpenPackagePage");
    public string OpenPackageSourceLabel => GetTranslation("Licenses_OpenPackageSource");
    public string PackageLinksHint => GetTranslation("Licenses_PackageLinks_Hint");
    public string Version => _notice?.Version ?? string.Empty;
    public string LicenseExpression => _notice?.LicenseExpression ?? string.Empty;
    public string PlatformText => _notice is null
        ? string.Empty
        : GetPlatformText(_notice.Platforms);
    public string PackageText => _notice is null || _notice.Packages.Count == 0
        ? GetTranslation("Licenses_Packages_BundledComponent")
        : string.Join(
            Environment.NewLine,
            _notice.Packages.Select(package => $"{package.Name} {package.Version}"));
    public bool HasPackages => _notice is { Packages.Count: > 0 };
    public bool ShowGeneralSourceLink => !HasPackages;
    public IReadOnlyList<ThirdPartyPackageLinkItem> PackageLinks => _notice is null
        ? Array.Empty<ThirdPartyPackageLinkItem>()
        : _notice.Packages.Select(package => new ThirdPartyPackageLinkItem(
            $"{package.Name} {package.Version}",
            package.PackagePageUri,
            package.SourceUri)).ToArray();
    public string CopyrightNotice => _notice?.CopyrightNotice ?? string.Empty;
    public string? RequiredNotice => _notice?.RequiredNotice;
    public bool HasRequiredNotice => !string.IsNullOrWhiteSpace(RequiredNotice);
    public string LicenseText => _notice?.LicenseText ?? string.Empty;
    public Uri? SourceUri => _notice?.SourceUri;

    public void SetNotice(ThirdPartyNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        _notice = notice;
        RaiseNoticeProperties();
    }

    protected override void OnLanguageChanged()
    {
        RaisePropertiesChanged(
        [
            nameof(Subtitle),
            nameof(BackLabel),
            nameof(VersionLabel),
            nameof(LicenseLabel),
            nameof(PlatformsLabel),
            nameof(PackagesLabel),
            nameof(CopyrightLabel),
            nameof(RequiredNoticeLabel),
            nameof(FullLicenseLabel),
            nameof(OpenSourcePageLabel),
            nameof(OpenPackagePageLabel),
            nameof(OpenPackageSourceLabel),
            nameof(PackageLinksHint),
            nameof(PlatformText),
            nameof(PackageText),
            nameof(PackageLinks)
        ]);
    }

    private void RaiseNoticeProperties() =>
        RaisePropertiesChanged(
        [
            nameof(Title),
            nameof(Version),
            nameof(LicenseExpression),
            nameof(PlatformText),
            nameof(PackageText),
            nameof(HasPackages),
            nameof(ShowGeneralSourceLink),
            nameof(PackageLinks),
            nameof(CopyrightNotice),
            nameof(RequiredNotice),
            nameof(HasRequiredNotice),
            nameof(LicenseText),
            nameof(SourceUri)
        ]);

    private string GetPlatformText(ThirdPartyPlatform platforms) => platforms switch
    {
        ThirdPartyPlatform.Windows => GetTranslation("Licenses_Platform_Windows"),
        ThirdPartyPlatform.Android => GetTranslation("Licenses_Platform_Android"),
        ThirdPartyPlatform.Windows | ThirdPartyPlatform.Android =>
            GetTranslation("Licenses_Platform_WindowsAndroid"),
        _ => GetTranslation("Licenses_Platform_Shared")
    };
}
