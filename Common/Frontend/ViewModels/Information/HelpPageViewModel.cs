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

    public HelpPageViewModel(UiPreferencesService preferences, HelpCatalog catalog, InformationRoute route,
        Action navigateBack, Action<InformationRoute> navigate) : base(preferences)
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
        RefreshContent();
    }

    public InformationRoute Route { get; }
    public ReactiveCommand<RxVoid, RxVoid> NavigateBackCommand { get; }
    public string Title { get; private set; } = string.Empty;
    public string Summary { get; private set; } = string.Empty;
    public string CategoryLabel { get; private set; } = string.Empty;
    public IReadOnlyList<HelpLinkItemViewModel> Cards => _cards;
    public IReadOnlyList<HelpSectionViewModel> Sections { get; private set; } = [];
    public bool HasSections => Sections.Count != 0;
    public bool HasCards => Cards.Count != 0;
    public bool HasCategoryLabel => CategoryLabel.Length != 0;
    public string BackLabel => GetTranslation("Common_Back");
    public string CardsLabel => GetTranslation(Route.Destination switch
    {
        InformationDestination.Help => "Help_Categories",
        InformationDestination.HelpCategory => "Help_Topics",
        _ => "Help_RelatedTopics"
    });
    public bool IsTopicUnavailable => _isTopicUnavailable;
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
        if (Route.Destination == InformationDestination.Help)
        {
            Title = GetTranslation("Help_Title");
            Summary = GetTranslation("Help_Subtitle");
            _cards = content.Categories.Select(category => new HelpLinkItemViewModel(
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
            nameof(BackLabel), nameof(TopicUnavailableText)
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
