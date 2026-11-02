using System.Collections.ObjectModel;
using PasswordManagerLocal.Common.Frontend.Services;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

/// <summary>One cancellable tag-assignment session. No password writes occur here.</summary>
public sealed class PasswordTagPickerViewModel : ViewModelBase
{
    private readonly List<PasswordTagPickerItemViewModel> _allTags = [];
    private readonly Action<IReadOnlyList<Guid>, string> _apply;
    private string _searchQuery = string.Empty;
    private string _suggestedColor = PasswordColorUtility.DefaultColor;
    private string? _colorSourceTagName;
    private bool _showSelectedOnly;
    private bool _hasRemovedTags;
    private bool _isActive;
    private bool _isCompactLayout;
    private bool _isUpdatingSelection;

    internal PasswordTagPickerViewModel(
        UiPreferencesService preferences,
        Action<IReadOnlyList<Guid>, string> apply,
        Action cancel,
        Action manage,
        Action create)
        : base(preferences)
    {
        _apply = apply;
        ApplyCommand = Own(ReactiveCommand.Create(Apply));
        CancelCommand = Own(ReactiveCommand.Create(cancel));
        ManageTagsCommand = Own(ReactiveCommand.Create(manage));
        CreateTagCommand = Own(ReactiveCommand.Create(create));
        ShowAllCommand = Own(ReactiveCommand.Create(() => { ShowSelectedOnly = false; }));
        ShowSelectedCommand = Own(ReactiveCommand.Create(() => { ShowSelectedOnly = true; }));
        ClearSearchCommand = Own(ReactiveCommand.Create(() => { SearchQuery = string.Empty; }));
        ClearSelectionCommand = Own(ReactiveCommand.Create(ClearSelection));
    }

    public ReactiveCommand<RxVoid, RxVoid> ApplyCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ManageTagsCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> CreateTagCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ShowAllCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ShowSelectedCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ClearSearchCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ClearSelectionCommand { get; }

    public ObservableCollection<PasswordTagPickerItemViewModel> Tags { get; private set; } = [];
    public bool IsActive => _isActive;
    public bool ShowPickerDetails => !_isCompactLayout;
    public bool ShowColorSuggestion => HasColorSuggestion && ShowPickerDetails;
    public bool ShowSelectionActions => ShowSelectedOnly && ShowPickerDetails;
    public bool ShowCancelButton => !IsAndroidUi || _isCompactLayout;

    internal void SetCompactLayout(bool compact)
    {
        if (_isCompactLayout == compact)
            return;
        _isCompactLayout = compact;
        RaisePropertiesChanged([nameof(ShowPickerDetails), nameof(ShowColorSuggestion),
            nameof(ShowSelectionActions), nameof(ShowCancelButton)]);
    }
    internal double ScrollOffset { get; set; }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            value ??= string.Empty;
            if (_searchQuery == value)
                return;
            this.RaiseAndSetIfChanged(ref _searchQuery, value);
            FilterTags(resetScroll: true);
            this.RaisePropertyChanged(nameof(HasSearchQuery));
        }
    }

    public bool ShowSelectedOnly
    {
        get => _showSelectedOnly;
        private set
        {
            if (_showSelectedOnly == value)
                return;
            this.RaiseAndSetIfChanged(ref _showSelectedOnly, value);
            this.RaisePropertyChanged(nameof(ShowAll));
            this.RaisePropertyChanged(nameof(ShowSelectionActions));
            FilterTags(resetScroll: true);
        }
    }

    public bool ShowAll => !ShowSelectedOnly;
    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);
    public bool HasTags => Tags.Count > 0;
    public bool HasNoTags => _allTags.Count == 0;
    public bool HasSelection => _allTags.Any(tag => tag.IsSelected);
    public bool IsFilteredEmpty => !HasNoTags && !HasTags;
    public bool HasColorSuggestion => _colorSourceTagName is not null;
    public bool HasRemovedTags => _hasRemovedTags;
    public string SuggestedColor => _suggestedColor;
    public string Title => GetTranslation("Passwords_TagPicker_Title");
    public string SearchPlaceholder => GetTranslation("Passwords_TagPicker_Search");
    public string AllLabel => GetTranslation("Passwords_TagPicker_All");
    public string SelectedLabel => string.Format(GetTranslation("Passwords_TagPicker_Selected"), SelectedCount);
    public string SelectedCountLabel => string.Format(GetTranslation("Passwords_TagPicker_Count"), SelectedCount);
    public string DoneLabel => GetTranslation("Passwords_TagPicker_Done");
    public string CancelLabel => GetTranslation("Common_Cancel");
    public string ManageTagsLabel => GetTranslation("Passwords_Editor_ManageTags");
    public string CreateTagLabel => GetTranslation("Passwords_Tags_Empty_Add");
    public string ClearSearchLabel => GetTranslation("Help_ClearSearch");
    public string ClearSelectionLabel => GetTranslation("Passwords_TagPicker_ClearSelection");
    public string NoTagsTitle => GetTranslation("Passwords_Tags_Empty_Title");
    public string NoTagsDescription => GetTranslation("Passwords_TagPicker_Empty");
    public string FilteredEmptyMessage => GetTranslation(HasSearchQuery
        ? "Passwords_Editor_NoMatchingTags"
        : "Passwords_TagPicker_NoneSelected");
    public string ColorSuggestionMessage => string.Format(
        GetTranslation("Passwords_TagPicker_ColorSuggestion"), _colorSourceTagName);
    public string RemovedTagsMessage => GetTranslation("Passwords_TagPicker_RemovedTags");
    public string ManagementHint => GetTranslation("Passwords_TagPicker_ManagementHint");
    private int SelectedCount => _allTags.Count(tag => tag.IsSelected);

    internal void Begin(IEnumerable<PasswordTagItemViewModel> tags, IEnumerable<Guid> selectedIds, string color)
    {
        _isActive = true;
        _searchQuery = string.Empty;
        _showSelectedOnly = false;
        _suggestedColor = color;
        _colorSourceTagName = null;
        _hasRemovedTags = false;
        ScrollOffset = 0;
        RebuildTags(tags, selectedIds.ToHashSet());
        this.RaisePropertyChanged(nameof(SearchQuery));
        this.RaisePropertyChanged(nameof(ShowSelectedOnly));
        this.RaisePropertyChanged(nameof(ShowAll));
        RaiseLabelsAndState();
    }

    internal void RefreshTags(IEnumerable<PasswordTagItemViewModel> tags)
    {
        if (!_isActive)
            return;
        var selectedIds = _allTags.Where(tag => tag.IsSelected).Select(tag => tag.Id).ToHashSet();
        RebuildTags(tags, selectedIds);
        RaiseLabelsAndState();
    }

    internal void End()
    {
        _isActive = false;
        _allTags.Clear();
        _colorSourceTagName = null;
        _hasRemovedTags = false;
        _suggestedColor = PasswordColorUtility.DefaultColor;
        _searchQuery = string.Empty;
        _showSelectedOnly = false;
        ScrollOffset = 0;
        FilterTags(resetScroll: false);
        RaiseLabelsAndState();
    }

    private void RebuildTags(IEnumerable<PasswordTagItemViewModel> tags, HashSet<Guid> selectedIds)
    {
        _allTags.Clear();
        foreach (var tag in tags.OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(tag => tag.Id))
            _allTags.Add(new PasswordTagPickerItemViewModel(tag, selectedIds.Contains(tag.Id), HandleSelectionChanged));
        if (selectedIds.Except(_allTags.Select(tag => tag.Id)).Any())
            _hasRemovedTags = true;
        FilterTags(resetScroll: false);
    }

    private void HandleSelectionChanged(PasswordTagPickerItemViewModel tag)
    {
        // Rebuilt rows must not be able to alter the active session through a stale binding.
        if (!_isActive || _isUpdatingSelection || !_allTags.Contains(tag))
            return;
        if (tag.IsSelected)
        {
            // Preserve the editor's existing "last newly selected tag suggests color" behavior.
            // Removing a tag intentionally does not rewind that suggestion.
            _suggestedColor = tag.Color;
            _colorSourceTagName = tag.Name;
        }
        if (ShowSelectedOnly)
            FilterTags(resetScroll: false);
        RaiseLabelsAndState();
    }

    private void FilterTags(bool resetScroll)
    {
        if (resetScroll)
            ScrollOffset = 0;
        var query = SearchQuery.Trim();
        Tags = new ObservableCollection<PasswordTagPickerItemViewModel>(_allTags.Where(tag =>
            (!ShowSelectedOnly || tag.IsSelected) &&
            (query.Length == 0 || tag.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))));
        this.RaisePropertyChanged(nameof(Tags));
        this.RaisePropertyChanged(nameof(HasTags));
        this.RaisePropertyChanged(nameof(HasNoTags));
        this.RaisePropertyChanged(nameof(IsFilteredEmpty));
        this.RaisePropertyChanged(nameof(FilteredEmptyMessage));
    }

    private void ClearSelection()
    {
        _isUpdatingSelection = true;
        try
        {
            foreach (var tag in _allTags.Where(tag => tag.IsSelected).ToArray())
                tag.IsSelected = false;
        }
        finally
        {
            _isUpdatingSelection = false;
        }
        if (ShowSelectedOnly)
            FilterTags(resetScroll: false);
        RaiseLabelsAndState();
    }

    private void Apply()
    {
        if (_isActive)
            _apply(_allTags.Where(tag => tag.IsSelected).Select(tag => tag.Id).Order().ToArray(), _suggestedColor);
    }

    protected override void OnLanguageChanged()
    {
        _allTags.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
        FilterTags(resetScroll: false);
        RaiseLabelsAndState();
    }

    private void RaiseLabelsAndState() => RaisePropertiesChanged([
        nameof(Title), nameof(SearchPlaceholder), nameof(AllLabel), nameof(SelectedLabel),
        nameof(SelectedCountLabel), nameof(DoneLabel), nameof(CancelLabel), nameof(ManageTagsLabel),
        nameof(CreateTagLabel), nameof(ClearSearchLabel), nameof(ClearSelectionLabel),
        nameof(NoTagsTitle), nameof(NoTagsDescription), nameof(FilteredEmptyMessage),
        nameof(ColorSuggestionMessage), nameof(RemovedTagsMessage), nameof(ManagementHint),
        nameof(HasSelection), nameof(HasColorSuggestion), nameof(HasRemovedTags),
        nameof(SuggestedColor), nameof(HasSearchQuery), nameof(ShowColorSuggestion), nameof(ShowSelectionActions)]);
}
