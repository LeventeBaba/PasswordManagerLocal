using System.Globalization;
using PasswordManagerLocal.Common.Frontend.Information;
using PasswordManagerLocal.Common.Frontend.Services;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Information;

public sealed class AboutViewModel : ViewModelBase
{
    private const string CreatorName = "Levente Baba";

    private readonly string? _applicationVersion;
    private readonly DateTime? _buildTimestampUtc = ApplicationVersionInfo.GetBuildTimestampUtc();

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

    public Uri SourceCodeUri { get; } = new("https://github.com/LeventeBaba/PasswordManagerLocal/tree/Demo");
    public Uri ProjectLicenseUri { get; } = new("https://github.com/LeventeBaba/PasswordManagerLocal/blob/master/LICENSE.txt");
    public Uri CreatorProfileUri { get; } = new("https://github.com/LeventeBaba");
    public Uri OnlinePrivacyPolicyUri => PrivacyPolicyLinks.ForLanguage(CurrentLanguage);
    public Uri FeedbackEmailUri { get; } = new($"mailto:{ApplicationBranding.DeveloperEmail}");
    public string FeedbackEmail => ApplicationBranding.DeveloperEmail;

    public string Title => GetTranslation("About_Title");
    public string Subtitle => GetTranslation("About_Subtitle");
    public string BackLabel => GetTranslation("Common_Back");
    public string ApplicationName => ApplicationBranding.DisplayName;
    public string ProductDescription => GetTranslation("About_ProductDescription");
    public string VersionText => _applicationVersion is null
        ? GetTranslation("About_Version_Unavailable")
        : string.Format(
            CultureInfo.CurrentCulture,
            GetTranslation("About_Version_Format"),
            _applicationVersion);
    public string BuildTimeText => _buildTimestampUtc is { } timestamp
        ? string.Format(
            CultureInfo.GetCultureInfo(CurrentLanguage == AppLanguage.Hungarian ? "hu-HU" : "en-US"),
            GetTranslation("About_BuildTime_Format"),
            timestamp)
        : GetTranslation("About_BuildTime_Unavailable");
    public string DemoLimitsNotice => string.Format(
        CultureInfo.CurrentCulture, GetTranslation("About_DemoLimits"),
        DemoLimits.PasswordsPerAccount, DemoLimits.TagsPerAccount, DemoLimits.CustomColorsPerAccount,
        DemoLimits.AccountsPerDevice, DemoLimits.DevicesPerAccount);
    public string DevelopmentNotice => GetTranslation("About_DevelopmentNotice");
    public string SecurityTitle => GetTranslation("About_Security_Title");
    public string SecurityDescription => GetTranslation("About_Security_Description");
    public string OnlinePrivacyPolicyLabel => GetTranslation("Privacy_OpenOnline");
    public string DatabaseProtectionDescription => GetTranslation("About_Security_DatabaseProtection");
    public string ProjectTitle => GetTranslation("About_Project_Title");
    public string CreatorText => string.Format(
        CultureInfo.CurrentCulture, GetTranslation("About_Creator_Format"), CreatorName);
    public string ProjectDescription => GetTranslation("About_Project_Description");
    public string SourceCodeLabel => GetTranslation("About_Project_SourceCode");
    public string ProjectLicenseLabel => GetTranslation("About_Project_License");
    public string CreatorProfileLabel => GetTranslation("About_Project_CreatorProfile");
    public string FeedbackTitle => GetTranslation("About_Feedback_Title");
    public string FeedbackDescription => GetTranslation("About_Feedback_Description");
    public string FeedbackSafetyNote => GetTranslation("About_Feedback_SafetyNote");
    public string OpenEmailLabel => GetTranslation("About_Feedback_OpenEmail");
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
            nameof(BuildTimeText),
            nameof(DemoLimitsNotice),
            nameof(DevelopmentNotice),
            nameof(SecurityTitle),
            nameof(SecurityDescription),
            nameof(DatabaseProtectionDescription),
            nameof(OnlinePrivacyPolicyUri),
            nameof(OnlinePrivacyPolicyLabel),
            nameof(ProjectTitle),
            nameof(CreatorText),
            nameof(ProjectDescription),
            nameof(SourceCodeLabel),
            nameof(ProjectLicenseLabel),
            nameof(CreatorProfileLabel),
            nameof(FeedbackTitle),
            nameof(FeedbackDescription),
            nameof(FeedbackSafetyNote),
            nameof(OpenEmailLabel),
            nameof(LicensesTitle),
            nameof(LicensesDescription)
        ]);
}
