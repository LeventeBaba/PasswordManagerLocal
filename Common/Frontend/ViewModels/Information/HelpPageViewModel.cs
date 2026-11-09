using System.Globalization;
using PasswordManagerLocal.Common.Frontend.Information;
using PasswordManagerLocal.Common.Frontend.Services;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Information;

/// <summary>Only bundled documentation and its reading position; never receives account or form data.</summary>
public sealed class HelpPageViewModel : ViewModelBase
{
    private readonly HelpCatalog _catalog;
    private readonly Action<InformationRoute> _navigate;
    private bool _isTopicUnavailable;
    private IReadOnlyList<HelpLinkItemViewModel> _cards = [];
    private string _searchText = string.Empty;
    private string? _categoryId;

    public HelpPageViewModel(UiPreferencesService preferences, HelpCatalog catalog, InformationRoute route,
        Action navigateBack, Action<InformationRoute> navigate, Action returnToApp) : base(preferences)
    {
        _catalog = catalog;
        _navigate = navigate;
        var content = _catalog.ForLanguage(CurrentLanguage);
        var valid = route.Destination switch
        {
            InformationDestination.Help => true,
            InformationDestination.HelpCategory => content.Categories.Any(category => category.Id == route.ContentId),
            InformationDestination.HelpTopic => route.ContentId is not null && content.Topics.ContainsKey(route.ContentId),
            _ => false
        };
        Route = valid ? route : new InformationRoute(InformationDestination.Help);
        _isTopicUnavailable = !valid;
        NavigateBackCommand = Own(ReactiveCommand.Create(navigateBack));
        ReturnToAppCommand = Own(ReactiveCommand.Create(returnToApp));
        FindHelpCommand = Own(ReactiveCommand.Create(() =>
            _navigate(new InformationRoute(InformationDestination.Help))));
        NavigateToCategoryCommand = Own(ReactiveCommand.Create(() =>
        {
            if (_categoryId is not null)
                _navigate(new InformationRoute(InformationDestination.HelpCategory, _categoryId));
        }));
        ClearSearchCommand = Own(ReactiveCommand.Create(() => { SearchText = string.Empty; }));
        RefreshContent();
    }

    public InformationRoute Route { get; }
    public ReactiveCommand<RxVoid, RxVoid> NavigateBackCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ReturnToAppCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> FindHelpCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> NavigateToCategoryCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ClearSearchCommand { get; }
    public bool IsHelpHome => Route.Destination == InformationDestination.Help;
    public bool CanFindHelp => !IsHelpHome;
    public string SearchText
    {
        get => _searchText;
        set
        {
            value ??= string.Empty;
            if (_searchText == value)
                return;
            this.RaiseAndSetIfChanged(ref _searchText, value);
            ScrollOffset = 0;
            FocusedCardId = null;
            RefreshContent();
        }
    }
    public bool IsSearchActive => IsHelpHome && !string.IsNullOrWhiteSpace(SearchText);
    public bool HasNoSearchResults => IsSearchActive && !HasCards;
    public bool ShowCardsLabel => HasCards || IsSearchActive;
    public string FindHelpLabel => GetTranslation("Help_FindHelp");
    public string ReturnToAppLabel => GetTranslation("Help_ReturnToApp");
    public string ReturnToAppHelpText => GetTranslation("Help_ReturnToApp_Help");
    public string SearchLabel => GetTranslation("Help_SearchLabel");
    public string SearchPlaceholder => GetTranslation("Help_SearchPlaceholder");
    public string SearchKeyboardHelpText => GetTranslation("Help_SearchKeyboardHelp");
    public string SearchResultsStatusText => HasNoSearchResults
        ? $"{CardsLabel}. {NoSearchResultsText}" : CardsLabel;
    public string ClearSearchLabel => GetTranslation("Help_ClearSearch");
    public string NoSearchResultsText => GetTranslation("Help_SearchNoResults");
    public string CategoryNavigationLabel => string.Format(CultureInfo.CurrentCulture,
        GetTranslation("Help_OpenCategory_Format"), CategoryLabel);
    public string Title { get; private set; } = string.Empty;
    public string Summary { get; private set; } = string.Empty;
    public string CategoryLabel { get; private set; } = string.Empty;
    public IReadOnlyList<HelpLinkItemViewModel> Cards => _cards;
    public IReadOnlyList<HelpSectionViewModel> Sections { get; private set; } = [];
    public bool HasSections => Sections.Count != 0;
    public bool HasCards => Cards.Count != 0;
    public bool HasCategoryLabel => CategoryLabel.Length != 0;
    public string BackLabel => GetTranslation("Common_Back");
    public string CardsLabel => IsSearchActive
        ? string.Format(CultureInfo.CurrentCulture, GetTranslation("Help_ResultsCount_Format"), Cards.Count)
        : GetTranslation(Route.Destination switch
    {
        InformationDestination.Help => "Help_Categories",
        InformationDestination.HelpCategory => "Help_Topics",
        _ => "Help_RelatedTopics"
    });
    public bool IsTopicUnavailable => _isTopicUnavailable && !IsSearchActive;
    public string TopicUnavailableText => GetTranslation("Help_TopicUnavailable");

    // Volatile view state for this public-document route, not persisted or logged.
    public double ScrollOffset { get; set; }
    public string? FocusedCardId { get; set; }

    public void SetTopicUnavailable(bool value)
    {
        _isTopicUnavailable = value;
        this.RaisePropertyChanged(nameof(IsTopicUnavailable));
    }

    protected override void OnLanguageChanged() => RefreshContent();

    private void RefreshContent()
    {
        var content = _catalog.ForLanguage(CurrentLanguage);
        foreach (var card in _cards)
            card.Dispose();
        Sections = [];
        CategoryLabel = string.Empty;
        _categoryId = null;
        if (Route.Destination == InformationDestination.Help)
        {
            Title = GetTranslation("Help_Title");
            Summary = GetTranslation("Help_Subtitle");
            _cards = IsSearchActive
                ? _catalog.Search(CurrentLanguage, IsAndroidUi ? HelpPlatform.Android : HelpPlatform.Windows, SearchText)
                    .Select(result => new HelpLinkItemViewModel(
                        new InformationRoute(InformationDestination.HelpTopic, result.Topic.Id),
                        result.Topic.Title, result.Excerpt, _navigate, result.CategoryTitle)).ToArray()
                : content.Categories.Select(category => new HelpLinkItemViewModel(
                new InformationRoute(InformationDestination.HelpCategory, category.Id),
                category.Title, category.Summary, _navigate)).ToArray();
        }
        else if (Route.Destination == InformationDestination.HelpCategory)
        {
            var category = content.Categories.Single(item => item.Id == Route.ContentId);
            Title = category.Title;
            Summary = category.Summary;
            _cards = content.Topics.Values.Where(topic => topic.CategoryId == category.Id).Select(CreateTopicLink).ToArray();
        }
        else
        {
            var topic = content.Topics[Route.ContentId!];
            Title = topic.Title;
            Summary = topic.Summary;
            CategoryLabel = content.Categories.Single(category => category.Id == topic.CategoryId).Title;
            _categoryId = topic.CategoryId;
            var step = 0;
            Sections = topic.Sections.Where(section => section.Platform == HelpPlatform.All ||
                    section.Platform == (IsAndroidUi ? HelpPlatform.Android : HelpPlatform.Windows))
                .Select(section => new HelpSectionViewModel(
                    section.Kind == HelpSectionKind.Step
                        ? string.Format(CultureInfo.CurrentCulture, GetTranslation("Help_Step_Format"), ++step, section.Title)
                        : section.Title,
                    section.Body,
                    section.Kind switch
                    {
                        HelpSectionKind.Warning => GetTranslation("Help_Warning"),
                        HelpSectionKind.Tip => GetTranslation("Help_Tip"),
                        _ => section.Platform == HelpPlatform.All ? string.Empty : section.Platform.ToString()
                    },
                    section.Kind == HelpSectionKind.Warning, section.Kind == HelpSectionKind.Tip)).ToArray();
            _cards = topic.RelatedTopicIds.Select(id => CreateTopicLink(content.Topics[id])).ToArray();
        }
        RaisePropertiesChanged([
            nameof(Title), nameof(Summary), nameof(CategoryLabel), nameof(HasCategoryLabel),
            nameof(Cards), nameof(Sections), nameof(HasCards), nameof(HasSections), nameof(CardsLabel),
            nameof(BackLabel), nameof(TopicUnavailableText), nameof(IsTopicUnavailable),
            nameof(IsSearchActive), nameof(HasNoSearchResults), nameof(ShowCardsLabel),
            nameof(FindHelpLabel), nameof(ReturnToAppLabel), nameof(ReturnToAppHelpText),
            nameof(SearchLabel), nameof(SearchPlaceholder), nameof(ClearSearchLabel),
            nameof(NoSearchResultsText), nameof(CategoryNavigationLabel),
            nameof(SearchKeyboardHelpText), nameof(SearchResultsStatusText)
        ]);
    }

    private HelpLinkItemViewModel CreateTopicLink(HelpTopic topic) => new(
        new InformationRoute(InformationDestination.HelpTopic, topic.Id), topic.Title, topic.Summary, _navigate);

    protected override void DisposeManaged()
    {
        foreach (var card in _cards)
            card.Dispose();
        base.DisposeManaged();
    }
}
