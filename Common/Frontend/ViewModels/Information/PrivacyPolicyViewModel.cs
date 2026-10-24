using System.Globalization;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Information;
using PasswordManagerLocal.Common.Frontend.Services;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Information;

public sealed class PrivacyPolicyViewModel : ViewModelBase
{
    private readonly PrivacyPolicyCatalog _catalog;

    public PrivacyPolicyViewModel(UiPreferencesService preferences, PrivacyPolicyCatalog catalog,
        Action navigateBack, Action returnToApp) : base(preferences)
    {
        _catalog = catalog;
        NavigateBackCommand = Own(ReactiveCommand.Create(navigateBack));
        ReturnToAppCommand = Own(ReactiveCommand.Create(returnToApp));
    }

    private PrivacyPolicyDocument Document => _catalog.ForLanguage(CurrentLanguage);
    public ReactiveCommand<RxVoid, RxVoid> NavigateBackCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ReturnToAppCommand { get; }
    public string Title => Document.Title;
    public string Summary => Document.Summary;
    public string StatusText => Document.StatusText;
    public string VersionText => string.Format(CultureInfo.CurrentCulture, GetTranslation("Privacy_Version_Format"), Document.Version);
    public string DateText => string.Format(CultureInfo.CurrentCulture,
        GetTranslation(Document.EffectiveOn is null ? "Privacy_Reviewed_Format" : "Privacy_Effective_Format"),
        (Document.EffectiveOn ?? Document.ReviewedOn).ToString("D",
            CultureInfo.GetCultureInfo(CurrentLanguage == AppLanguage.Hungarian ? "hu-HU" : "en-US")));
    public IReadOnlyList<PrivacyPolicySection> Sections => Document.Sections;
    public Uri ComplaintAuthorityUri { get; } = new("https://www.naih.hu/");
    public string ComplaintAuthorityLabel => GetTranslation("Privacy_AuthorityLink");
    public string ExternalLinkHelpText => GetTranslation("Privacy_ExternalLinkHelp");
    public string BackLabel => GetTranslation("Common_Back");
    public string ReturnToAppLabel => GetTranslation("Help_ReturnToApp");

    // Public-document reading position only, kept in memory for this UI session.
    public double ScrollOffset { get; set; }

    protected override void OnLanguageChanged() => RaisePropertiesChanged([
        nameof(Title), nameof(Summary), nameof(StatusText), nameof(VersionText), nameof(DateText),
        nameof(Sections), nameof(BackLabel), nameof(ReturnToAppLabel),
        nameof(ComplaintAuthorityLabel), nameof(ExternalLinkHelpText)
    ]);
}
