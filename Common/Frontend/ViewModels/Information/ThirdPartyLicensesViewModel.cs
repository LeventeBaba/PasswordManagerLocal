using System.Globalization;
using PasswordManagerLocal.Common.Frontend.Information;
using PasswordManagerLocal.Common.Frontend.Services;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Information;

public sealed class ThirdPartyLicensesViewModel : ViewModelBase
{
    private readonly ThirdPartyLicenseItemViewModel[] _allItems;
    private string _searchText = string.Empty;
    private IReadOnlyList<ThirdPartyLicenseItemViewModel> _filteredItems;

    public ThirdPartyLicensesViewModel(
        UiPreferencesService uiPreferences,
        ThirdPartyNoticeCatalog catalog,
        Action navigateBack,
        Action<ThirdPartyNotice> openNotice)
        : base(uiPreferences)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(navigateBack);
        ArgumentNullException.ThrowIfNull(openNotice);
        ReviewedDate = catalog.ReviewedDate;
        _allItems = catalog.Notices
            .Select(notice => new ThirdPartyLicenseItemViewModel(
                notice,
                GetPlatformText,
                openNotice))
            .ToArray();
        _filteredItems = _allItems;
        NavigateBackCommand = Own(ReactiveCommand.Create(navigateBack));
        ClearSearchCommand = Own(ReactiveCommand.Create(() =>
        {
            SearchText = string.Empty;
        }));
    }

    public ReactiveCommand<RxVoid, RxVoid> NavigateBackCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ClearSearchCommand { get; }
    public DateOnly ReviewedDate { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            var normalized = value ?? string.Empty;
            if (_searchText == normalized)
                return;
            this.RaiseAndSetIfChanged(ref _searchText, normalized);
            this.RaisePropertyChanged(nameof(HasSearchText));
            ApplyFilter();
        }
    }

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);
    public IReadOnlyList<ThirdPartyLicenseItemViewModel> FilteredItems
    {
        get => _filteredItems;
        private set => this.RaiseAndSetIfChanged(ref _filteredItems, value);
    }

    public bool HasResults => FilteredItems.Count != 0;
    public bool HasNoResults => !HasResults;
    public string Title => GetTranslation("Licenses_Title");
    public string Subtitle => GetTranslation("Licenses_Subtitle");
    public string BackLabel => GetTranslation("Common_Back");
    public string SearchPlaceholder => GetTranslation("Licenses_Search_Placeholder");
    public string ClearSearchLabel => GetTranslation("Licenses_Search_Clear");
    public string EmptyResultsText => GetTranslation("Licenses_EmptyResults");
    public string ResultSummary => string.Format(
        CultureInfo.CurrentCulture,
        GetTranslation("Licenses_ResultCount_Format"),
        FilteredItems.Count);
    public string ReviewedText => string.Format(
        CultureInfo.CurrentCulture,
        GetTranslation("Licenses_Reviewed_Format"),
        ReviewedDate.ToString("d", CultureInfo.CurrentCulture));

    protected override void OnLanguageChanged()
    {
        foreach (var item in _allItems)
            item.RefreshLocalization();
        RaisePropertiesChanged(
        [
            nameof(Title),
            nameof(Subtitle),
            nameof(BackLabel),
            nameof(SearchPlaceholder),
            nameof(ClearSearchLabel),
            nameof(EmptyResultsText),
            nameof(ResultSummary),
            nameof(ReviewedText)
        ]);
    }

    protected override void DisposeManaged()
    {
        foreach (var item in _allItems)
            item.Dispose();
        base.DisposeManaged();
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        FilteredItems = query.Length == 0
            ? _allItems
            : _allItems
                .Where(item => item.SearchText.Contains(
                    query,
                    StringComparison.CurrentCultureIgnoreCase))
                .ToArray();
        this.RaisePropertyChanged(nameof(HasResults));
        this.RaisePropertyChanged(nameof(HasNoResults));
        this.RaisePropertyChanged(nameof(ResultSummary));
    }

    private string GetPlatformText(ThirdPartyPlatform platforms) => platforms switch
    {
        ThirdPartyPlatform.Windows => GetTranslation("Licenses_Platform_Windows"),
        ThirdPartyPlatform.Android => GetTranslation("Licenses_Platform_Android"),
        ThirdPartyPlatform.Windows | ThirdPartyPlatform.Android =>
            GetTranslation("Licenses_Platform_WindowsAndroid"),
        _ => GetTranslation("Licenses_Platform_Shared")
    };
}
