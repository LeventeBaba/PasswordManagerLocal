using Avalonia.Media;
using PasswordManagerLocal.Common.Frontend.Abstractions.Services;
using PasswordManagerLocal.Common.Frontend.Helpers;
using PasswordManagerLocal.Common.Frontend.Security;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Constants;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;
using ReactiveUI;
using System.Collections.ObjectModel;
using System.ComponentModel;
using ReactiveUI.Primitives;
using System.Security.Cryptography;
using System.Text;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

public sealed partial class PasswordsViewModel : ViewModelBase
{
    private const string ListPane = "list";
    private const string EditorPane = "editor";
    private const string DetailsPane = "details";
    private const string CustomColorListPane = "custom-color-list";
    private const string ColorPane = "color";
    private const string ExportTargetPane = "export-target";
    private const string CustomColorKey = "custom";
    private const string SelectedCustomColorKey = "selected-custom";
    private const int MaximumVisibleEditorTagSuggestions = 8;

    private static readonly string[] LocalizedPropertyNames =
    [
        nameof(Title),
        nameof(Subtitle),
        nameof(AddPasswordButtonLabel),
        nameof(AddPasswordIconLabel),
        nameof(RefreshButtonLabel),
        nameof(EmptyStateTitle),
        nameof(EmptyStateDescription),
        nameof(EmptyStateAddLabel),
        nameof(SearchEmptyTitle),
        nameof(SearchEmptyDescription),
        nameof(DetailsTitle),
        nameof(DetailsEmptyTitle),
        nameof(DetailsEmptyDescription),
        nameof(DetailsTagsLabel),
        nameof(DetailsNoTagsMessage),
        nameof(NameLabel),
        nameof(PasswordLabel),
        nameof(DescriptionLabel),
        nameof(ColorLabel),
        nameof(CurrentColorCodeLabel),
        nameof(MoreColorsLabel),
        nameof(ColorPickerTitle),
        nameof(ColorPickerNameLabel),
        nameof(ColorPickerNamePlaceholder),
        nameof(AddColorButtonLabel),
        nameof(CustomColorSaveSuccessMessage),
        nameof(ColorPickerDescription),
        nameof(ColorPickerCodeLabel),
        nameof(ColorPickerCodePlaceholder),
        nameof(ApplyColorCodeLabel),
        nameof(BackToPasswordEditorLabel),
        nameof(AlphaLabel),
        nameof(RedLabel),
        nameof(GreenLabel),
        nameof(BlueLabel),
        nameof(CreatedAtLabel),
        nameof(UpdatedAtLabel),
        nameof(RevealPasswordLabel),
        nameof(HidePasswordLabel),
        nameof(CopyPasswordLabel),
        nameof(RevealEditorPasswordLabel),
        nameof(EditPasswordLabel),
        nameof(DeletePasswordLabel),
        nameof(EditorTitle),
        nameof(SavePasswordButtonLabel),
        nameof(CancelButtonLabel),
        nameof(BackToListLabel),
        nameof(EditorNamePlaceholder),
        nameof(EditorDescriptionPlaceholder),
        nameof(EditorPasswordPlaceholder),
        nameof(EditorPasswordHint),
        nameof(EditorPasswordVisibilityToggleText),
        nameof(EditorTagsLabel),
        nameof(EditorManageTagsLabel),
        nameof(EditorManageTagsUnavailableToolTip),
        nameof(EditorTagSearchPlaceholder),
        nameof(EditorNoTagsAvailableMessage),
        nameof(EditorNoMatchingTagsMessage),
        nameof(EditorAllTagsSelectedMessage),
        nameof(PasswordTagsTitle),
        nameof(PasswordTagsBackToEditorLabel),
        nameof(PasswordTagsEmptyTitle),
        nameof(PasswordTagsEmptyDescription),
        nameof(PasswordTagsEmptyAddLabel),
        nameof(PasswordTagsSearchEmptyTitle),
        nameof(PasswordTagsSearchEmptyDescription),
        nameof(PasswordTagsSearchPlaceholder),
        nameof(PasswordTagsSearchModeLabel),
        nameof(PasswordTagsSearchModeNameLabel),
        nameof(PasswordTagsSearchModeColorLabel),
        nameof(PasswordTagsSortLabel),
        nameof(PasswordTagsSortNameAscMenuLabel),
        nameof(PasswordTagsSortNameDescMenuLabel),
        nameof(AddPasswordTagLabel),
        nameof(PasswordTagEditorTitle),
        nameof(PasswordTagEditorNameLabel),
        nameof(PasswordTagEditorNamePlaceholder),
        nameof(PasswordTagEditorColorLabel),
        nameof(PasswordTagEditorSaveLabel),
        nameof(PasswordTagEditorBackLabel),
        nameof(PasswordTagDeleteConfirmationTitle),
        nameof(PasswordTagDeleteConfirmationMessage),
        nameof(ConfirmDeletePasswordTagLabel),
        nameof(SearchLabel),
        nameof(SearchPlaceholder),
        nameof(SearchModeLabel),
        nameof(SearchModeNameLabel),
        nameof(SearchModeDescriptionLabel),
        nameof(SearchModeTagLabel),
        nameof(CustomColorsTitle),
        nameof(CustomColorsBackToEditorLabel),
        nameof(CustomColorsEmptyTitle),
        nameof(CustomColorsEmptyDescription),
        nameof(CustomColorsEmptyAddLabel),
        nameof(CustomColorsSearchEmptyTitle),
        nameof(CustomColorsSearchEmptyDescription),
        nameof(CustomColorsSearchPlaceholder),
        nameof(CustomColorsSearchModeLabel),
        nameof(CustomColorsSearchModeNameLabel),
        nameof(CustomColorsSearchModeColorCodeLabel),
        nameof(CustomColorsSortLabel),
        nameof(CustomColorsSortNameAscMenuLabel),
        nameof(CustomColorsSortNameDescMenuLabel),
        nameof(AddCustomColorLabel),
        nameof(CustomColorDeleteConfirmationTitle),
        nameof(CustomColorDeleteConfirmationMessage),
        nameof(ConfirmDeleteCustomColorLabel),
        nameof(CustomColorDeleteSuccessMessage),
        nameof(BackFromColorPickerLabel),
        nameof(SwitchOnLabel),
        nameof(SwitchOffLabel),
        nameof(SortLabel),
        nameof(ClearSelectionLabel),
        nameof(MultiSelectionCancelLabel),
        nameof(MultiSelectionSelectAllLabel),
        nameof(MultiSelectionExportLabel),
        nameof(MultiSelectionDeleteLabel),
        nameof(MultiSelectionExportToolTip),
        nameof(MultiSelectionDeleteToolTip),
        nameof(ExportTargetTitle),
        nameof(ExportTargetSubtitle),
        nameof(ExportTargetAccountsLabel),
        nameof(ExportTargetBackLabel),
        nameof(ExportTargetEmptyTitle),
        nameof(ExportTargetEmptyDescription),
        nameof(ExportConfirmationTitle),
        nameof(ExportConfirmationMessage),
        nameof(ExportDeleteOriginalLabel),
        nameof(ExportDeleteOriginalDescription),
        nameof(ConfirmExportLabel),
        nameof(PasswordRevealHint),
        nameof(DeleteConfirmationTitle),
        nameof(DeleteConfirmationMessage),
        nameof(ConfirmDeletePasswordLabel),
        nameof(ListTabLabel),
        nameof(EditorTabLabel),
        nameof(DetailsTabLabel),
        nameof(EditorClosedTitle),
        nameof(EditorClosedDescription),
        nameof(IsEditorPasswordVisibilityToggleVisible),
        nameof(PasswordStrengthLabel),
        nameof(PasswordStrengthInfoTitle),
        nameof(PasswordStrengthInfoBody),
        nameof(PasswordStrengthInfoAccessibleLabel),
        nameof(GeneratePasswordLabel),
    ];

    private readonly IEndpoints _endpoints;
    private readonly IAuthSessionRegistry _authSessionRegistry;
    private readonly PasswordStrengthEstimator _passwordStrengthEstimator = new();
    private readonly MaximumStrengthPasswordGenerator _passwordGenerator;
    private readonly DebouncedUiAction _searchDebounce = new(TimeSpan.FromMilliseconds(125));
    private readonly DebouncedUiAction _customColorSearchDebounce = new(TimeSpan.FromMilliseconds(125));
    private readonly List<PasswordItemViewModel> _allPasswords = [];
    private readonly List<PasswordTagItemViewModel> _allPasswordTags = [];
    private readonly List<CustomUserColorInfoResponse> _savedCustomColors = [];
    private readonly List<CustomColorItemViewModel> _allCustomColors = [];

    private Guid _token;
    private PasswordItemViewModel? _selectedPassword;
    private PasswordItemViewModel? _passwordPendingDeletion;
    private CustomColorItemViewModel? _customColorPendingDeletion;
    private IReadOnlyList<PasswordItemViewModel> _passwordsPendingDeletion = [];
    private IReadOnlyList<CustomColorItemViewModel> _customColorsPendingDeletion = [];
    private string? _revealedPassword;
    private string _currentPane = ListPane;
    private PasswordPaneTransitionViewModel? _currentPaneViewModel;
    private bool _isPaneTransitionReversed;
    private bool _isCreateMode;
    private bool _isDeleteConfirmationOpen;
    private bool _isCustomColorDeleteConfirmationOpen;
    private bool _isSavingPassword;
    private bool _isDeletingPassword;
    private bool _isDeletingCustomColor;
    private bool _isSavingCustomColor;
    private Guid? _editingCustomColorId;
    private string _editorName = string.Empty;
    private string _editorDescription = string.Empty;
    private string _editorColor = PasswordColorUtility.DefaultColor;
    private string _editorPassword = string.Empty;
    private string _editorTagSearchQuery = string.Empty;
    private bool _isEditorTagSearchFocused;
    private bool _isEditorPasswordVisible;
    private bool _isEditorStoredPasswordRevealed;
    private int _editorPasswordStrength;
    private int _revealedPasswordStrength;
    private string _customColorPickerName = string.Empty;
    private Color _customColorPickerColor = Color.FromRgb(20, 184, 166);
    private string _customColorPickerCode = PasswordColorUtility.DefaultColor;
    private bool _isUpdatingCustomColorPickerFields;
    private string _customColorCode = PasswordColorUtility.DefaultColor;
    private double _customAlpha = 255;
    private double _customRed = 20;
    private double _customGreen = 184;
    private double _customBlue = 166;
    private bool _isUpdatingColorFields;
    private string _searchQuery = string.Empty;
    private string _customColorSearchQuery = string.Empty;
    private bool _isPasswordSearchNameEnabled = true;
    private bool _isPasswordSearchDescriptionEnabled = true;
    private bool _isPasswordSearchTagEnabled = true;
    private bool _isCustomColorSearchNameEnabled = true;
    private bool _isCustomColorSearchCodeEnabled = true;
    private string _customColorSortKey = "name-asc";
    private PasswordColorOptionViewModel? _selectedEditorColorOption;
    private PasswordSortOptionViewModel? _selectedSortOption;
    private bool _isPasswordMultiSelectionActive;
    private bool _isCustomColorMultiSelectionActive;
    private MultiSelectionExportKind _pendingExportKind;
    private IReadOnlyList<Guid> _pendingExportItemIds = [];
    private Guid _selectedExportTargetToken;
    private string _selectedExportTargetDisplayName = string.Empty;
    private bool _isExportConfirmationOpen;
    private bool _deleteOriginalOnExport;
    private bool _isExporting;

    public PasswordsViewModel(
        UiPreferencesService uiPreferences,
        IEndpoints endpoints,
        IAuthSessionRegistry authSessionRegistry)
        : base(uiPreferences)
    {
        _endpoints = endpoints;
        _authSessionRegistry = authSessionRegistry;
        _passwordGenerator = new MaximumStrengthPasswordGenerator(_passwordStrengthEstimator);

        Passwords = new ObservableCollection<PasswordItemViewModel>();
        SelectedPasswordTags = new ObservableCollection<PasswordTagItemViewModel>();
        EditorSelectedTags = new ObservableCollection<PasswordTagItemViewModel>();
        EditorTagSuggestions = new ObservableCollection<PasswordTagItemViewModel>();
        CustomColors = new ObservableCollection<CustomColorItemViewModel>();
        ExportTargetProfiles = new ObservableCollection<ExportTargetProfileItemViewModel>();
        PresetColors = new ObservableCollection<PasswordColorOptionViewModel>();
        SortOptions = new ObservableCollection<PasswordSortOptionViewModel>();

        SelectEditorTagCommand = Own(ReactiveCommand.Create<PasswordTagItemViewModel>(SelectEditorTag));
        RemoveEditorTagCommand = Own(ReactiveCommand.Create<PasswordTagItemViewModel>(RemoveEditorTag));
        ViewPasswordCommand = Own(ReactiveCommand.CreateFromTask<PasswordItemViewModel>(BeginViewPasswordAsync));
        EditPasswordCommand = Own(ReactiveCommand.CreateFromTask<PasswordItemViewModel>(BeginEditPasswordAsync));
        DeletePasswordCommand = Own(ReactiveCommand.CreateFromTask<PasswordItemViewModel>(BeginDeletePasswordAsync));
        EditCustomColorCommand = Own(ReactiveCommand.Create<CustomColorItemViewModel>(OpenCustomColorPickerForEditing));
        DeleteCustomColorCommand = Own(ReactiveCommand.CreateFromTask<CustomColorItemViewModel>(BeginDeleteCustomColorAsync));
        EditPasswordTagCommand = Own(ReactiveCommand.Create<PasswordTagManagementItemViewModel>(OpenPasswordTagEditorForEditing));
        DeletePasswordTagCommand = Own(ReactiveCommand.CreateFromTask<PasswordTagManagementItemViewModel>(BeginDeletePasswordTagAsync));
        RefreshCommand = Own(ReactiveCommand.CreateFromTask(async () => { await RefreshAsync(true); }));
        ExecutePrimaryActionCommand = Own(ReactiveCommand.CreateFromTask(ExecutePrimaryActionAsync));
        SearchCommand = Own(ReactiveCommand.Create(ApplyCurrentSearch));
        SelectSortOptionCommand = Own(ReactiveCommand.Create<string>(SelectSortOptionByKey));
        BeginCreatePasswordCommand = Own(ReactiveCommand.Create(BeginCreatePassword));
        EditSelectedPasswordCommand = Own(ReactiveCommand.CreateFromTask(EditSelectedPasswordAsync));
        BeginDeleteSelectedPasswordCommand = Own(ReactiveCommand.Create(BeginDeleteSelectedPassword));
        ConfirmDeletePasswordCommand = Own(ReactiveCommand.CreateFromTask(ConfirmDeletePasswordAsync));
        CancelDeletePasswordCommand = Own(ReactiveCommand.Create(CancelDeletePassword));
        RevealPasswordCommand = Own(ReactiveCommand.CreateFromTask(RevealPasswordAsync));
        HidePasswordCommand = Own(ReactiveCommand.Create(HidePassword));
        CopyRevealedPasswordCommand = Own(ReactiveCommand.CreateFromTask(CopyRevealedPasswordAsync));
        RevealEditorPasswordCommand = Own(ReactiveCommand.CreateFromTask(RevealEditorPasswordAsync));
        SavePasswordCommand = Own(ReactiveCommand.CreateFromTask(SavePasswordAsync));
        CancelPasswordEditorCommand = Own(ReactiveCommand.Create(CancelPasswordEditor));
        ToggleEditorPasswordVisibilityCommand = Own(ReactiveCommand.Create(ToggleEditorPasswordVisibility));
        GenerateEditorPasswordCommand = Own(ReactiveCommand.Create(GenerateEditorPassword));
        OpenCustomColorPickerCommand = Own(ReactiveCommand.Create(OpenCustomColorPicker));
        BackFromColorPickerCommand = Own(ReactiveCommand.Create(BackFromColorPicker));
        SaveCustomColorCommand = Own(ReactiveCommand.CreateFromTask(SaveCustomColorAsync));
        BackFromCustomColorListCommand = Own(ReactiveCommand.Create(BackFromCustomColorList));
        SearchCustomColorsCommand = Own(ReactiveCommand.Create(ApplyCustomColorFiltersAndSorting));
        SelectCustomColorSortOptionCommand = Own(ReactiveCommand.Create<string>(SelectCustomColorSortOption));
        ConfirmDeleteCustomColorCommand = Own(ReactiveCommand.CreateFromTask(ConfirmDeleteCustomColorAsync));
        CancelDeleteCustomColorCommand = Own(ReactiveCommand.Create(CancelDeleteCustomColor));
        ApplyManualColorCodeCommand = Own(ReactiveCommand.Create(ApplyManualColorCode));
        BackToListCommand = Own(ReactiveCommand.Create(BackToList));
        ClearSelectionCommand = Own(ReactiveCommand.Create(BackToList));
        CancelMultiSelectionCommand = Own(ReactiveCommand.Create(CancelMultiSelection));
        SelectAllMultiSelectionCommand = Own(ReactiveCommand.Create(SelectAllMultiSelection));
        ExportMultiSelectionCommand = Own(ReactiveCommand.Create(BeginExportMultiSelection));
        BeginDeleteMultiSelectionCommand = Own(ReactiveCommand.Create(BeginDeleteMultiSelection));
        BackFromExportTargetCommand = Own(ReactiveCommand.Create(BackFromExportTarget));
        ConfirmExportCommand = Own(ReactiveCommand.CreateFromTask(ConfirmExportAsync));
        CancelExportConfirmationCommand = Own(ReactiveCommand.Create(CancelExportConfirmation));
        InitializePasswordTagManagement();

        RebuildPresetColors();
        RebuildSortOptions();
        SelectDefaultPresetColor();
        SelectDefaultSortOption();
    }

    public ReactiveCommand<PasswordTagItemViewModel, RxVoid> SelectEditorTagCommand { get; }
    public ReactiveCommand<PasswordTagItemViewModel, RxVoid> RemoveEditorTagCommand { get; }
    public ReactiveCommand<PasswordItemViewModel, RxVoid> ViewPasswordCommand { get; }
    public ReactiveCommand<PasswordItemViewModel, RxVoid> EditPasswordCommand { get; }
    public ReactiveCommand<PasswordItemViewModel, RxVoid> DeletePasswordCommand { get; }
    public ReactiveCommand<CustomColorItemViewModel, RxVoid> EditCustomColorCommand { get; }
    public ReactiveCommand<CustomColorItemViewModel, RxVoid> DeleteCustomColorCommand { get; }
    public ReactiveCommand<PasswordTagManagementItemViewModel, RxVoid> EditPasswordTagCommand { get; }
    public ReactiveCommand<PasswordTagManagementItemViewModel, RxVoid> DeletePasswordTagCommand { get; }

    public ObservableCollection<PasswordItemViewModel> Passwords { get; private set; }

    public ObservableCollection<PasswordTagItemViewModel> SelectedPasswordTags { get; }

    public ObservableCollection<PasswordTagItemViewModel> EditorSelectedTags { get; }

    public ObservableCollection<PasswordTagItemViewModel> EditorTagSuggestions { get; }

    public ObservableCollection<CustomColorItemViewModel> CustomColors { get; private set; }

    public ObservableCollection<ExportTargetProfileItemViewModel> ExportTargetProfiles { get; }

    public bool HasExportTargetProfiles => ExportTargetProfiles.Count > 0;

    public bool IsExportTargetProfilesEmpty => ExportTargetProfiles.Count == 0;

    public bool IsPasswordSelectionModeInactive => !IsPasswordMultiSelectionActive;

    public bool IsCustomColorSelectionModeInactive => !IsCustomColorMultiSelectionActive;

    public bool IsPasswordMultiSelectionActive
    {
        get => _isPasswordMultiSelectionActive;
        private set
        {
            if (_isPasswordMultiSelectionActive == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _isPasswordMultiSelectionActive, value);
            this.RaisePropertyChanged(nameof(IsPasswordSelectionModeInactive));
            RaiseMultiSelectionStateChanged();
        }
    }

    public bool IsCustomColorMultiSelectionActive
    {
        get => _isCustomColorMultiSelectionActive;
        private set
        {
            if (_isCustomColorMultiSelectionActive == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _isCustomColorMultiSelectionActive, value);
            this.RaisePropertyChanged(nameof(IsCustomColorSelectionModeInactive));
            RaiseMultiSelectionStateChanged();
        }
    }

    public bool IsMultiSelectionToolbarVisible =>
        IsPasswordMultiSelectionActive
        || IsCustomColorMultiSelectionActive
        || IsPasswordTagMultiSelectionActive;

    public bool HasSelectedMultiSelectionItems => IsPasswordMultiSelectionActive
        ? _allPasswords.Any(item => item.IsSelected)
        : IsCustomColorMultiSelectionActive
            ? _allCustomColors.Any(item => item.IsSelected)
            : IsPasswordTagMultiSelectionActive && _allManagedPasswordTags.Any(item => item.IsSelected);

    public bool AreAllVisibleMultiSelectionItemsSelected => IsPasswordMultiSelectionActive
        ? Passwords.Count > 0 && Passwords.All(item => item.IsSelected)
        : IsCustomColorMultiSelectionActive
            ? CustomColors.Count > 0 && CustomColors.All(item => item.IsSelected)
            : IsPasswordTagMultiSelectionActive
              && PasswordTags.Count > 0
              && PasswordTags.All(item => item.IsSelected);

    public bool IsSelectAllMultiSelectionAction => !AreAllVisibleMultiSelectionItemsSelected;

    public bool IsDeselectAllMultiSelectionAction => AreAllVisibleMultiSelectionItemsSelected;

    public bool CanExportMultiSelectionItems =>
        HasSelectedMultiSelectionItems && HasOtherActiveExportTargetAccount();

    public bool IsExportConfirmationOpen
    {
        get => _isExportConfirmationOpen;
        private set => this.RaiseAndSetIfChanged(ref _isExportConfirmationOpen, value);
    }

    public bool DeleteOriginalOnExport
    {
        get => _deleteOriginalOnExport;
        set
        {
            if (_deleteOriginalOnExport == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _deleteOriginalOnExport, value);
            this.RaisePropertyChanged(nameof(ConfirmExportLabel));
        }
    }

    public event EventHandler? ListScrollToTopRequested;

    public ObservableCollection<PasswordColorOptionViewModel> PresetColors { get; }

    public ObservableCollection<PasswordSortOptionViewModel> SortOptions { get; }

    public PasswordItemViewModel? SelectedPassword
    {
        get => _selectedPassword;
        set
        {
            if (ReferenceEquals(_selectedPassword, value))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedPassword, value);
            this.RaisePropertyChanged(nameof(HasSelection));
            this.RaisePropertyChanged(nameof(IsSelectionEmpty));
            this.RaisePropertyChanged(nameof(IsPasswordHidden));

            HidePassword();
            RefreshSelectedPasswordTags();

            if (value is not null && CurrentPane == ListPane)
            {
                CurrentPane = DetailsPane;
            }
        }
    }

    public bool HasSelection => SelectedPassword is not null;

    public bool IsSelectionEmpty => !HasSelection;

    public bool HasSelectedPasswordTags => SelectedPasswordTags.Count > 0;

    public bool HasNoSelectedPasswordTags => !HasSelectedPasswordTags;

    public PasswordItemViewModel? PasswordPendingDeletion
    {
        get => _passwordPendingDeletion;
        private set
        {
            this.RaiseAndSetIfChanged(ref _passwordPendingDeletion, value);
            this.RaisePropertyChanged(nameof(DeleteConfirmationTitle));
            this.RaisePropertyChanged(nameof(DeleteConfirmationMessage));
            this.RaisePropertyChanged(nameof(PasswordPendingDeletionName));
        }
    }

    public string PasswordPendingDeletionName => PasswordPendingDeletion?.Name ?? string.Empty;

    public string? RevealedPassword
    {
        get => _revealedPassword;
        private set
        {
            this.RaiseAndSetIfChanged(ref _revealedPassword, value);
            this.RaisePropertyChanged(nameof(HasRevealedPassword));
            this.RaisePropertyChanged(nameof(IsPasswordHidden));
            RefreshRevealedPasswordStrength();
        }
    }

    public bool HasRevealedPassword => !string.IsNullOrEmpty(RevealedPassword);

    public bool IsPasswordHidden => HasSelection && !HasRevealedPassword;

    public int RevealedPasswordStrength => _revealedPasswordStrength;

    public bool HasPasswords => Passwords.Count > 0;

    public bool IsEmpty => Passwords.Count == 0;

    public bool HasStoredPasswords => _allPasswords.Count > 0;

    public bool IsVaultEmpty => _allPasswords.Count == 0;

    public bool IsSearchResultEmpty => HasStoredPasswords && Passwords.Count == 0;

    public string CurrentPane
    {
        get => _currentPane;
        private set => SetCurrentPane(value, false);
    }

    public PasswordPaneTransitionViewModel CurrentPaneViewModel
    {
        get => _currentPaneViewModel ??= CreatePaneTransitionViewModel(CurrentPane);
        private set => this.RaiseAndSetIfChanged(ref _currentPaneViewModel, value);
    }

    public bool IsPaneTransitionReversed
    {
        get => _isPaneTransitionReversed;
        private set => this.RaiseAndSetIfChanged(ref _isPaneTransitionReversed, value);
    }

    public bool IsListPaneVisible => CurrentPane == ListPane;

    public bool IsEditorPaneVisible => CurrentPane == EditorPane;

    public bool IsDetailsPaneVisible => CurrentPane == DetailsPane;

    public bool IsCustomColorListPaneVisible => CurrentPane == CustomColorListPane;

    public bool IsColorPaneVisible => CurrentPane == ColorPane;

    public bool IsExportTargetPaneVisible => CurrentPane == ExportTargetPane;

    public bool IsEditorOpen => IsEditorPaneVisible;

    public bool IsEditorClosed => !IsEditorPaneVisible;

    private void SetCurrentPane(string value, bool isBackNavigation)
    {
        if (string.Equals(_currentPane, value, StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(_currentPane, ListPane, StringComparison.Ordinal)
            && !string.Equals(value, ListPane, StringComparison.Ordinal))
        {
            ExitPasswordMultiSelection();
        }

        if (string.Equals(_currentPane, CustomColorListPane, StringComparison.Ordinal)
            && !string.Equals(value, CustomColorListPane, StringComparison.Ordinal))
        {
            ExitCustomColorMultiSelection();
        }

        if (string.Equals(_currentPane, PasswordTagListPane, StringComparison.Ordinal)
            && !string.Equals(value, PasswordTagListPane, StringComparison.Ordinal))
        {
            ExitPasswordTagMultiSelection();
        }

        IsPaneTransitionReversed = isBackNavigation;
        ClearStatusMessage();
        this.RaiseAndSetIfChanged(ref _currentPane, value);
        this.RaisePropertyChanged(nameof(IsListPaneVisible));
        this.RaisePropertyChanged(nameof(IsEditorPaneVisible));
        this.RaisePropertyChanged(nameof(IsDetailsPaneVisible));
        this.RaisePropertyChanged(nameof(IsCustomColorListPaneVisible));
        this.RaisePropertyChanged(nameof(IsColorPaneVisible));
        this.RaisePropertyChanged(nameof(IsPasswordTagListPaneVisible));
        this.RaisePropertyChanged(nameof(IsPasswordTagEditorPaneVisible));
        this.RaisePropertyChanged(nameof(IsExportTargetPaneVisible));
        this.RaisePropertyChanged(nameof(IsEditorOpen));
        this.RaisePropertyChanged(nameof(IsEditorClosed));
        CurrentPaneViewModel = CreatePaneTransitionViewModel(value);
    }

    private PasswordPaneTransitionViewModel CreatePaneTransitionViewModel(string pane) =>
        pane switch
        {
            EditorPane => new PasswordEditorPaneTransitionViewModel(this),
            DetailsPane => new PasswordDetailsPaneTransitionViewModel(this),
            CustomColorListPane => new PasswordCustomColorListPaneTransitionViewModel(this),
            ColorPane => new PasswordColorPaneTransitionViewModel(this),
            PasswordTagListPane => new PasswordTagListPaneTransitionViewModel(this),
            PasswordTagEditorPane => new PasswordTagEditorPaneTransitionViewModel(this),
            ExportTargetPane => new PasswordExportTargetPaneTransitionViewModel(this),
            _ => new PasswordListPaneTransitionViewModel(this)
        };

    public bool IsCreateMode
    {
        get => _isCreateMode;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isCreateMode, value);
            this.RaisePropertyChanged(nameof(IsEditMode));
            this.RaisePropertyChanged(nameof(EditorTitle));
            this.RaisePropertyChanged(nameof(SavePasswordButtonLabel));
            this.RaisePropertyChanged(nameof(EditorPasswordHint));
            this.RaisePropertyChanged(nameof(CanRevealEditorStoredPassword));
            this.RaisePropertyChanged(nameof(IsEditorPasswordFieldVisible));
            this.RaisePropertyChanged(nameof(IsEditorPasswordVisibilityToggleVisible));
            this.RaisePropertyChanged(nameof(IsEditorPasswordStrengthVisible));
        }
    }

    public bool IsEditMode => !IsCreateMode;

    public bool IsDeleteConfirmationOpen
    {
        get => _isDeleteConfirmationOpen;
        private set => this.RaiseAndSetIfChanged(ref _isDeleteConfirmationOpen, value);
    }

    public string EditorName
    {
        get => _editorName;
        set
        {
            this.RaiseAndSetIfChanged(ref _editorName, value);
            RefreshEditorPasswordStrength();
        }
    }

    public string EditorDescription
    {
        get => _editorDescription;
        set => this.RaiseAndSetIfChanged(ref _editorDescription, value);
    }

    public string EditorTagSearchQuery
    {
        get => _editorTagSearchQuery;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_editorTagSearchQuery, value, StringComparison.Ordinal))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _editorTagSearchQuery, value);
            RefreshEditorTagSuggestions();
        }
    }

    public bool IsEditorTagSearchFocused
    {
        get => _isEditorTagSearchFocused;
        set
        {
            if (_isEditorTagSearchFocused == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _isEditorTagSearchFocused, value);
            RaiseEditorTagStateChanged();
        }
    }

    public bool HasAvailablePasswordTags => _allPasswordTags.Count > 0;

    public bool HasSelectedEditorTags => EditorSelectedTags.Count > 0;

    public bool AreAllEditorTagsSelected =>
        HasAvailablePasswordTags && EditorSelectedTags.Count >= _allPasswordTags.Count;

    public bool IsEditorTagSearchEnabled =>
        HasAvailablePasswordTags && !AreAllEditorTagsSelected;

    public bool IsEditorTagSuggestionPanelVisible =>
        IsEditorTagSearchFocused && IsEditorTagSearchEnabled;

    public bool HasEditorTagSuggestions => EditorTagSuggestions.Count > 0;

    public bool IsEditorTagNoMatchesVisible =>
        IsEditorTagSuggestionPanelVisible
        && !HasEditorTagSuggestions
        && !string.IsNullOrWhiteSpace(EditorTagSearchQuery);

    public bool IsEditorTagEmptyStateVisible => !HasAvailablePasswordTags;

    public bool IsEditorAllTagsSelectedVisible => AreAllEditorTagsSelected;

    public string EditorColor
    {
        get => _editorColor;
        private set
        {
            this.RaiseAndSetIfChanged(ref _editorColor, value);
            this.RaisePropertyChanged(nameof(EditorColorBrush));
            this.RaisePropertyChanged(nameof(EditorColorCode));
        }
    }

    public PasswordColorOptionViewModel? SelectedEditorColorOption
    {
        get => _selectedEditorColorOption;
        set
        {
            if (value?.IsManageColorsOption == true)
            {
                OpenCustomColorList();
                return;
            }

            if (ReferenceEquals(_selectedEditorColorOption, value))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedEditorColorOption, value);

            if (value is not null)
            {
                ApplyEditorColor(value.HexValue);
            }
        }
    }

    public IBrush EditorColorBrush => PasswordColorUtility.ParseBrush(EditorColor);

    public string EditorColorCode => EditorColor;

    public string CustomColorPickerName
    {
        get => _customColorPickerName;
        set => this.RaiseAndSetIfChanged(ref _customColorPickerName, value ?? string.Empty);
    }

    public Color CustomColorPickerColor
    {
        get => _customColorPickerColor;
        set
        {
            if (_customColorPickerColor == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _customColorPickerColor, value);
            SyncCustomColorPickerCodeFromColor();
        }
    }

    public string CustomColorPickerCode
    {
        get => _customColorPickerCode;
        set
        {
            value ??= string.Empty;
            if (_customColorPickerCode == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _customColorPickerCode, value);

            if (_isUpdatingCustomColorPickerFields
                || !PasswordColorUtility.TryNormalizeHexColor(value, out var normalizedColor))
            {
                return;
            }

            SetCustomColorPickerColorFromNormalizedCode(normalizedColor);
            ClearStatusMessage();
        }
    }

    public int CustomColorNameMaxLength => DataLengthConstants.CustomUserColorNameMaxLength;

    public string CustomColorCode
    {
        get => _customColorCode;
        set
        {
            value ??= string.Empty;
            this.RaiseAndSetIfChanged(ref _customColorCode, value);
        }
    }

    public double CustomAlpha
    {
        get => _customAlpha;
        set
        {
            var normalized = PasswordColorUtility.NormalizeComponent(value);
            if (Math.Abs(_customAlpha - normalized) < 0.01)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _customAlpha, normalized);
            this.RaisePropertyChanged(nameof(CustomAlphaText));
            ApplyColorFromSliders();
        }
    }

    public double CustomRed
    {
        get => _customRed;
        set
        {
            var normalized = PasswordColorUtility.NormalizeComponent(value);
            if (Math.Abs(_customRed - normalized) < 0.01)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _customRed, normalized);
            this.RaisePropertyChanged(nameof(CustomRedText));
            ApplyColorFromSliders();
        }
    }

    public double CustomGreen
    {
        get => _customGreen;
        set
        {
            var normalized = PasswordColorUtility.NormalizeComponent(value);
            if (Math.Abs(_customGreen - normalized) < 0.01)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _customGreen, normalized);
            this.RaisePropertyChanged(nameof(CustomGreenText));
            ApplyColorFromSliders();
        }
    }

    public double CustomBlue
    {
        get => _customBlue;
        set
        {
            var normalized = PasswordColorUtility.NormalizeComponent(value);
            if (Math.Abs(_customBlue - normalized) < 0.01)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _customBlue, normalized);
            this.RaisePropertyChanged(nameof(CustomBlueText));
            ApplyColorFromSliders();
        }
    }

    public string CustomAlphaText => PasswordColorUtility.ToComponentByte(CustomAlpha).ToString();

    public string CustomRedText => PasswordColorUtility.ToComponentByte(CustomRed).ToString();

    public string CustomGreenText => PasswordColorUtility.ToComponentByte(CustomGreen).ToString();

    public string CustomBlueText => PasswordColorUtility.ToComponentByte(CustomBlue).ToString();

    public string EditorPassword
    {
        get => _editorPassword;
        set
        {
            this.RaiseAndSetIfChanged(ref _editorPassword, value);
            RefreshEditorPasswordStrength();
        }
    }

    public int EditorPasswordStrength => _editorPasswordStrength;

    public bool IsEditorPasswordVisible
    {
        get => _isEditorPasswordVisible;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isEditorPasswordVisible, value);
            this.RaisePropertyChanged(nameof(EditorPasswordMaskCharacter));
            this.RaisePropertyChanged(nameof(EditorPasswordVisibilityToggleText));
        }
    }

    public bool IsEditorStoredPasswordRevealed
    {
        get => _isEditorStoredPasswordRevealed;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isEditorStoredPasswordRevealed, value);
            this.RaisePropertyChanged(nameof(CanRevealEditorStoredPassword));
            this.RaisePropertyChanged(nameof(IsEditorPasswordFieldVisible));
            this.RaisePropertyChanged(nameof(IsEditorPasswordVisibilityToggleVisible));
            this.RaisePropertyChanged(nameof(IsEditorPasswordStrengthVisible));
        }
    }

    public bool CanRevealEditorStoredPassword => IsEditMode && !IsEditorStoredPasswordRevealed;

    public bool IsEditorPasswordFieldVisible => IsCreateMode || IsEditorStoredPasswordRevealed;

    public bool IsEditorPasswordVisibilityToggleVisible => IsEditorPasswordFieldVisible;

    public bool IsEditorPasswordStrengthVisible => IsCreateMode || IsEditorStoredPasswordRevealed;

    public char EditorPasswordMaskCharacter => IsEditorPasswordVisible ? '\0' : '●';

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_searchQuery, value, StringComparison.Ordinal))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _searchQuery, value);
            _searchDebounce.Schedule(() => ApplyFiltersAndSorting(SelectedPassword?.Id, preserveSelection: true));
        }
    }

    public bool IsPasswordSearchNameEnabled
    {
        get => _isPasswordSearchNameEnabled;
        set => SetPasswordSearchMode(ref _isPasswordSearchNameEnabled, value, nameof(IsPasswordSearchNameEnabled));
    }

    public bool IsPasswordSearchDescriptionEnabled
    {
        get => _isPasswordSearchDescriptionEnabled;
        set => SetPasswordSearchMode(ref _isPasswordSearchDescriptionEnabled, value, nameof(IsPasswordSearchDescriptionEnabled));
    }

    public bool IsPasswordSearchTagEnabled
    {
        get => _isPasswordSearchTagEnabled;
        set => SetPasswordSearchMode(ref _isPasswordSearchTagEnabled, value, nameof(IsPasswordSearchTagEnabled));
    }

    public bool CanTogglePasswordSearchName => CanTogglePasswordSearchMode(_isPasswordSearchNameEnabled);

    public bool CanTogglePasswordSearchDescription => CanTogglePasswordSearchMode(_isPasswordSearchDescriptionEnabled);

    public bool CanTogglePasswordSearchTag => CanTogglePasswordSearchMode(_isPasswordSearchTagEnabled);

    public string CustomColorSearchQuery
    {
        get => _customColorSearchQuery;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_customColorSearchQuery, value, StringComparison.Ordinal))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _customColorSearchQuery, value);
            _customColorSearchDebounce.Schedule(ApplyCustomColorFiltersAndSorting);
        }
    }

    public bool IsCustomColorSearchNameEnabled
    {
        get => _isCustomColorSearchNameEnabled;
        set => SetCustomColorSearchMode(ref _isCustomColorSearchNameEnabled, value, nameof(IsCustomColorSearchNameEnabled));
    }

    public bool IsCustomColorSearchCodeEnabled
    {
        get => _isCustomColorSearchCodeEnabled;
        set => SetCustomColorSearchMode(ref _isCustomColorSearchCodeEnabled, value, nameof(IsCustomColorSearchCodeEnabled));
    }

    public bool CanToggleCustomColorSearchName => CanToggleCustomColorSearchMode(_isCustomColorSearchNameEnabled);

    public bool CanToggleCustomColorSearchCode => CanToggleCustomColorSearchMode(_isCustomColorSearchCodeEnabled);

    public bool HasCustomColors => CustomColors.Count > 0;

    public bool HasStoredCustomColors => _allCustomColors.Count > 0;

    public bool IsCustomColorListEmpty => _allCustomColors.Count == 0;

    public bool IsCustomColorSearchResultEmpty => HasStoredCustomColors && CustomColors.Count == 0;

    public CustomColorItemViewModel? CustomColorPendingDeletion
    {
        get => _customColorPendingDeletion;
        private set
        {
            this.RaiseAndSetIfChanged(ref _customColorPendingDeletion, value);
            this.RaisePropertyChanged(nameof(CustomColorDeleteConfirmationTitle));
            this.RaisePropertyChanged(nameof(CustomColorDeleteConfirmationMessage));
        }
    }

    public bool IsCustomColorDeleteConfirmationOpen
    {
        get => _isCustomColorDeleteConfirmationOpen;
        private set => this.RaiseAndSetIfChanged(ref _isCustomColorDeleteConfirmationOpen, value);
    }

    public PasswordSortOptionViewModel? SelectedSortOption
    {
        get => _selectedSortOption;
        set
        {
            if (ReferenceEquals(_selectedSortOption, value))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedSortOption, value);
            UpdateSortOptionSelectionMarks();
            RaiseSortMenuLabelProperties();
            ApplyFiltersAndSorting(SelectedPassword?.Id, preserveSelection: true);
        }
    }

    public ReactiveCommand<RxVoid, RxVoid> RefreshCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ExecutePrimaryActionCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> SearchCommand { get; }

    public ReactiveCommand<string, RxVoid> SelectSortOptionCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> BeginCreatePasswordCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> EditSelectedPasswordCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> BeginDeleteSelectedPasswordCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ConfirmDeletePasswordCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> CancelDeletePasswordCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> RevealPasswordCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> HidePasswordCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> CopyRevealedPasswordCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> RevealEditorPasswordCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> SavePasswordCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> CancelPasswordEditorCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ToggleEditorPasswordVisibilityCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> GenerateEditorPasswordCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> OpenCustomColorPickerCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> BackFromColorPickerCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> SaveCustomColorCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> BackFromCustomColorListCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> SearchCustomColorsCommand { get; }

    public ReactiveCommand<string, RxVoid> SelectCustomColorSortOptionCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ConfirmDeleteCustomColorCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> CancelDeleteCustomColorCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ApplyManualColorCodeCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> BackToListCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ClearSelectionCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> CancelMultiSelectionCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> SelectAllMultiSelectionCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ExportMultiSelectionCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> BeginDeleteMultiSelectionCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> BackFromExportTargetCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ConfirmExportCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> CancelExportConfirmationCommand { get; }

    public string Title => GetTranslation("Passwords_Title");

    public string Subtitle => GetTranslation("Passwords_Subtitle");

    public string AddPasswordButtonLabel => GetTranslation("Passwords_Add");

    public string AddPasswordIconLabel => GetTranslation("Passwords_Add_Icon");

    public string RefreshButtonLabel => GetTranslation("Common_Refresh");

    public string EmptyStateTitle => GetTranslation("Passwords_Empty_Title");

    public string EmptyStateDescription => GetTranslation("Passwords_Empty_Description");

    public string EmptyStateAddLabel => GetTranslation("Passwords_Empty_Add");

    public string SearchEmptyTitle => GetTranslation("Passwords_SearchEmpty_Title");

    public string SearchEmptyDescription => GetTranslation("Passwords_SearchEmpty_Description");

    public string DetailsTitle => GetTranslation("Passwords_Details_Title");

    public string DetailsEmptyTitle => GetTranslation("Passwords_Details_Empty_Title");

    public string DetailsEmptyDescription => GetTranslation("Passwords_Details_Empty_Description");

    public string DetailsTagsLabel => GetTranslation("Passwords_Details_TagsLabel");

    public string DetailsNoTagsMessage => GetTranslation("Passwords_Details_NoTags");

    public string NameLabel => GetTranslation("Common_Name");

    public string PasswordLabel => GetTranslation("Common_Password");

    public string DescriptionLabel => GetTranslation("Common_Description");

    public string ColorLabel => GetTranslation("Common_Color");

    public string CurrentColorCodeLabel => GetTranslation("Passwords_ColorPicker_CurrentColor");

    public string MoreColorsLabel => GetTranslation("Passwords_Color_More");

    public string ColorPickerTitle => GetTranslation(
        IsEditingCustomColor ? "Passwords_ColorPicker_EditTitle" : "Passwords_ColorPicker_Title");

    public string ColorPickerNameLabel => GetTranslation("Passwords_ColorPicker_NameLabel");

    public string ColorPickerNamePlaceholder => GetTranslation("Passwords_ColorPicker_NamePlaceholder");

    public string AddColorButtonLabel => GetTranslation(
        IsEditingCustomColor ? "Passwords_ColorPicker_Save" : "Passwords_ColorPicker_Add");

    public string CustomColorSaveSuccessMessage => GetTranslation(
        IsEditingCustomColor ? "Passwords_CustomColors_Update_Success" : "Passwords_CustomColors_Add_Success");

    public bool IsEditingCustomColor => _editingCustomColorId.HasValue;

    public string ColorPickerDescription => GetTranslation("Passwords_ColorPicker_Description");

    public string ColorPickerCodeLabel => GetTranslation("Passwords_ColorPicker_CodeLabel");

    public string ColorPickerCodePlaceholder => GetTranslation("Passwords_ColorPicker_CodePlaceholder");

    public string ApplyColorCodeLabel => GetTranslation("Passwords_ColorPicker_ApplyCode");

    public string BackToPasswordEditorLabel => GetTranslation("Passwords_ColorPicker_BackToEditor");

    public string AlphaLabel => GetTranslation("Common_Alpha");

    public string RedLabel => GetTranslation("Common_Red");

    public string GreenLabel => GetTranslation("Common_Green");

    public string BlueLabel => GetTranslation("Common_Blue");

    public string CreatedAtLabel => GetTranslation("Common_CreatedAt");

    public string UpdatedAtLabel => GetTranslation("Common_UpdatedAt");

    public string RevealPasswordLabel => GetTranslation("Passwords_Reveal");

    public string HidePasswordLabel => GetTranslation("Passwords_Hide");

    public string CopyPasswordLabel => GetTranslation("Passwords_Copy");

    public string RevealEditorPasswordLabel => GetTranslation("Passwords_Editor_RevealStoredPassword");

    public string EditPasswordLabel => GetTranslation("Common_Edit");

    public string DeletePasswordLabel => GetTranslation("Common_Delete");

    public string EditorTitle => GetTranslation(IsCreateMode ? "Passwords_Editor_CreateTitle" : "Passwords_Editor_EditTitle");

    public string SavePasswordButtonLabel => GetTranslation(IsCreateMode ? "Common_Add" : "Common_Save");

    public string CancelButtonLabel => GetTranslation("Common_Cancel");

    public string BackToListLabel => GetTranslation("Passwords_BackToList");

    public string EditorNamePlaceholder => GetTranslation("Passwords_Editor_NamePlaceholder");

    public string EditorDescriptionPlaceholder => GetTranslation("Passwords_Editor_DescriptionPlaceholder");

    public string EditorPasswordPlaceholder => GetTranslation("Passwords_Editor_PasswordPlaceholder");

    public string EditorPasswordHint => GetTranslation("Passwords_Editor_PasswordHint_Create");

    public string EditorPasswordVisibilityToggleText => GetTranslation(IsEditorPasswordVisible ? "Common_Hide" : "Common_Show");

    public string EditorTagsLabel => GetTranslation("Passwords_Editor_TagsLabel");

    public string EditorManageTagsLabel => GetTranslation("Passwords_Editor_ManageTags");

    public string EditorManageTagsUnavailableToolTip =>
        GetTranslation("Passwords_Editor_ManageTagsUnavailableToolTip");

    public string EditorTagSearchPlaceholder => GetTranslation("Passwords_Editor_TagSearchPlaceholder");

    public string EditorNoTagsAvailableMessage => GetTranslation("Passwords_Editor_NoTagsAvailable");

    public string EditorNoMatchingTagsMessage => GetTranslation("Passwords_Editor_NoMatchingTags");

    public string EditorAllTagsSelectedMessage => GetTranslation("Passwords_Editor_AllTagsSelected");

    public string PasswordStrengthLabel => GetTranslation("PasswordStrength_Label");

    public string PasswordStrengthInfoTitle => GetTranslation("PasswordStrength_Info_Title");

    public string PasswordStrengthInfoBody => GetTranslation("PasswordStrength_Info_Body");

    public string PasswordStrengthInfoAccessibleLabel => GetTranslation("PasswordStrength_Info_AccessibleLabel");

    public string GeneratePasswordLabel => GetTranslation("Passwords_Editor_GeneratePassword");

    public string SearchLabel => GetTranslation("Common_Search");

    public string SearchPlaceholder => GetTranslation("Passwords_Search_Placeholder");

    public string SearchModeLabel => GetTranslation("Passwords_SearchMode_Label");

    public string SearchModeNameLabel => GetTranslation("Common_Name");

    public string SearchModeDescriptionLabel => GetTranslation("Common_Description");

    public string SearchModeTagLabel => GetTranslation("Passwords_SearchMode_Tag");

    public string CustomColorsTitle => GetTranslation("Passwords_CustomColors_Title");

    public string CustomColorsBackToEditorLabel => GetTranslation(
        _customColorListReturnPane == PasswordTagEditorPane
            ? "Passwords_CustomColors_BackToTagEditor"
            : "Passwords_CustomColors_BackToEditor");

    public string CustomColorsEmptyTitle => GetTranslation("Passwords_CustomColors_Empty_Title");

    public string CustomColorsEmptyDescription => GetTranslation("Passwords_CustomColors_Empty_Description");

    public string CustomColorsEmptyAddLabel => GetTranslation("Passwords_CustomColors_Empty_Add");

    public string CustomColorsSearchEmptyTitle => GetTranslation("Passwords_CustomColors_SearchEmpty_Title");

    public string CustomColorsSearchEmptyDescription => GetTranslation("Passwords_CustomColors_SearchEmpty_Description");

    public string CustomColorsSearchPlaceholder => GetTranslation("Passwords_CustomColors_Search_Placeholder");

    public string CustomColorsSearchModeLabel => GetTranslation("Passwords_CustomColors_SearchMode_Label");

    public string CustomColorsSearchModeNameLabel => GetTranslation("Common_Name");

    public string CustomColorsSearchModeColorCodeLabel => GetTranslation("Passwords_CustomColors_SearchMode_ColorCode");

    public string CustomColorsSortLabel => GetTranslation("Passwords_CustomColors_Sort_Label");

    public string CustomColorsSortNameAscMenuLabel => BuildCustomColorSortMenuLabel("name-asc", "Passwords_Sort_NameAsc");

    public string CustomColorsSortNameDescMenuLabel => BuildCustomColorSortMenuLabel("name-desc", "Passwords_Sort_NameDesc");

    public string AddCustomColorLabel => GetTranslation("Passwords_CustomColors_Add");

    public string CustomColorDeleteConfirmationTitle => GetTranslation(
        _customColorsPendingDeletion.Count > 1
            ? "Passwords_CustomColors_DeleteConfirm_MultipleTitle"
            : "Passwords_CustomColors_DeleteConfirm_Title");

    public string CustomColorDeleteConfirmationMessage => _customColorsPendingDeletion.Count > 1
        ? string.Format(
            GetTranslation("Passwords_CustomColors_DeleteConfirm_MultipleMessage"),
            _customColorsPendingDeletion.Count)
        : string.Format(
            GetTranslation("Passwords_CustomColors_DeleteConfirm_Message"),
            CustomColorPendingDeletion?.DisplayName ?? string.Empty);

    public string ConfirmDeleteCustomColorLabel => GetTranslation("Passwords_CustomColors_DeleteConfirm_Confirm");

    public string CustomColorDeleteSuccessMessage => GetTranslation("Passwords_CustomColors_Delete_Success");

    public string BackFromColorPickerLabel => GetTranslation("Passwords_CustomColors_BackFromPicker");

    public string SwitchOnLabel => GetTranslation("Common_On");

    public string SwitchOffLabel => GetTranslation("Common_Off");

    public string SortLabel => GetTranslation("Passwords_Sort_Label");

    public string SortNameAscMenuLabel => BuildSortMenuLabel("name-asc", "Passwords_Sort_NameAsc");

    public string SortNameDescMenuLabel => BuildSortMenuLabel("name-desc", "Passwords_Sort_NameDesc");

    public string SortCreatedNewestMenuLabel => BuildSortMenuLabel("created-desc", "Passwords_Sort_CreatedNewest");

    public string SortCreatedOldestMenuLabel => BuildSortMenuLabel("created-asc", "Passwords_Sort_CreatedOldest");

    public string SortUpdatedNewestMenuLabel => BuildSortMenuLabel("updated-desc", "Passwords_Sort_UpdatedNewest");

    public string SortUpdatedOldestMenuLabel => BuildSortMenuLabel("updated-asc", "Passwords_Sort_UpdatedOldest");

    public string ClearSelectionLabel => GetTranslation("Common_ClearSelection");

    public string MultiSelectionCancelLabel => GetTranslation("Common_Cancel");

    public string MultiSelectionSelectAllLabel => GetTranslation(
        IsDeselectAllMultiSelectionAction ? "Common_DeselectAll" : "Common_SelectAll");

    public string MultiSelectionExportLabel => GetTranslation("Common_Export");

    public string MultiSelectionDeleteLabel => GetTranslation("Common_Delete");

    public string MultiSelectionExportToolTip => !HasSelectedMultiSelectionItems
        ? GetTranslation("Passwords_MultiSelection_Disabled_NoSelection")
        : !HasOtherActiveExportTargetAccount()
            ? GetTranslation("Passwords_MultiSelection_Export_Disabled_NoOtherAccount")
            : MultiSelectionExportLabel;

    public string MultiSelectionDeleteToolTip => HasSelectedMultiSelectionItems
        ? MultiSelectionDeleteLabel
        : GetTranslation("Passwords_MultiSelection_Disabled_NoSelection");

    public string ExportTargetTitle => GetTranslation("Passwords_Export_Target_Title");

    public string ExportTargetSubtitle => GetTranslation(_pendingExportKind switch
    {
        MultiSelectionExportKind.Passwords when _pendingExportItemIds.Count == 1 => "Passwords_Export_Target_Subtitle_Password",
        MultiSelectionExportKind.Passwords => "Passwords_Export_Target_Subtitle_Passwords",
        MultiSelectionExportKind.CustomColors when _pendingExportItemIds.Count == 1 => "Passwords_Export_Target_Subtitle_CustomColor",
        MultiSelectionExportKind.CustomColors => "Passwords_Export_Target_Subtitle_CustomColors",
        MultiSelectionExportKind.PasswordTags when _pendingExportItemIds.Count == 1 => "Passwords_Export_Target_Subtitle_Tag",
        _ => "Passwords_Export_Target_Subtitle_Tags"
    });

    public string ExportTargetAccountsLabel => GetTranslation("Passwords_Export_Target_Accounts");

    public string ExportTargetBackLabel => GetTranslation("Common_Back");

    public string ExportTargetEmptyTitle => GetTranslation("Passwords_Export_Target_Empty_Title");

    public string ExportTargetEmptyDescription => GetTranslation("Passwords_Export_Target_Empty_Description");

    public string ExportConfirmationTitle => GetTranslation("Passwords_Export_Confirm_Title");

    public string ExportConfirmationMessage => string.Format(
        GetTranslation(_pendingExportKind switch
        {
            MultiSelectionExportKind.Passwords when _pendingExportItemIds.Count == 1 => "Passwords_Export_Confirm_Message_Password",
            MultiSelectionExportKind.Passwords => "Passwords_Export_Confirm_Message_Passwords",
            MultiSelectionExportKind.CustomColors when _pendingExportItemIds.Count == 1 => "Passwords_Export_Confirm_Message_CustomColor",
            MultiSelectionExportKind.CustomColors => "Passwords_Export_Confirm_Message_CustomColors",
            MultiSelectionExportKind.PasswordTags when _pendingExportItemIds.Count == 1 => "Passwords_Export_Confirm_Message_Tag",
            _ => "Passwords_Export_Confirm_Message_Tags"
        }),
        _selectedExportTargetDisplayName);

    public string ExportDeleteOriginalLabel => GetTranslation("Passwords_Export_DeleteOriginal_Label");

    public string ExportDeleteOriginalDescription => GetTranslation("Passwords_Export_DeleteOriginal_Description");

    public string ConfirmExportLabel => GetTranslation(DeleteOriginalOnExport ? "Common_Move" : "Common_Copy");

    public string PasswordRevealHint => GetTranslation("Passwords_Reveal_Hint");

    public string DeleteConfirmationTitle => GetTranslation(
        _passwordsPendingDeletion.Count > 1
            ? "Passwords_DeleteConfirm_MultipleTitle"
            : "Passwords_DeleteConfirm_Title");

    public string DeleteConfirmationMessage => _passwordsPendingDeletion.Count > 1
        ? string.Format(GetTranslation("Passwords_DeleteConfirm_MultipleMessage"), _passwordsPendingDeletion.Count)
        : string.Format(GetTranslation("Passwords_DeleteConfirm_Message"), PasswordPendingDeletionName);

    public string ConfirmDeletePasswordLabel => GetTranslation("Passwords_DeleteConfirm_Confirm");

    public string ListTabLabel => GetTranslation("Passwords_Tab_List");

    public string EditorTabLabel => GetTranslation("Passwords_Tab_Editor");

    public string DetailsTabLabel => GetTranslation("Passwords_Tab_Details");

    public string EditorClosedTitle => GetTranslation("Passwords_Editor_Closed_Title");

    public string EditorClosedDescription => GetTranslation("Passwords_Editor_Closed_Description");

    protected override void OnLanguageChanged()
    {
        RaisePropertiesChanged(LocalizedPropertyNames);

        foreach (var password in _allPasswords)
        {
            password.ApplyActionLabels(EditPasswordLabel, DeletePasswordLabel);

            if (string.Equals(
                PasswordColorUtility.NormalizeKnownColor(password.Color),
                PasswordColorUtility.DefaultColor,
                StringComparison.OrdinalIgnoreCase))
            {
                password.ApplyColorName(GetTranslation("Passwords_Color_Teal"));
            }
        }

        foreach (var tag in _allPasswordTags)
            tag.ApplyRemoveLabel(GetEditorRemoveTagLabel(tag.Name));

        foreach (var customColor in _allCustomColors)
            customColor.ApplyDeleteLabel(DeletePasswordLabel);

        var currentEditorColor = EditorColor;
        var selectedSortKey = SelectedSortOption?.Key;
        RebuildPresetColors();
        RebuildSortOptions();
        ApplyEditorColor(currentEditorColor);
        SelectedSortOption = SortOptions.FirstOrDefault(item => item.Key == selectedSortKey)
            ?? SortOptions.FirstOrDefault();
        UpdateSortOptionSelectionMarks();
        RaiseSortMenuLabelProperties();
        RaiseCustomColorSortMenuLabelProperties();
        ApplyPasswordTagLocalization();
        ApplyFiltersAndSorting(SelectedPassword?.Id, preserveSelection: true);
        ApplyCustomColorFiltersAndSorting();
    }

    public async Task<bool> LoadAsync(Guid token)
    {
        _token = token;
        RaiseMultiSelectionStateChanged();
        return await RefreshAsync(false);
    }

    public void SetSessionToken(Guid token)
    {
        _token = token;
        RaiseMultiSelectionStateChanged();
    }

    public async Task<bool> RefreshCurrentDataAsync(bool showSuccessMessage = true) => await RefreshAsync(showSuccessMessage);

    public void RequestListScrollToTop() => ListScrollToTopRequested?.Invoke(this, EventArgs.Empty);

    public void SelectFirstEditorTagSuggestion()
    {
        if (EditorTagSuggestions.FirstOrDefault() is { } tag)
        {
            SelectEditorTag(tag);
        }
    }

    public void ShowMainPage()
    {
        ExitPasswordMultiSelection();
        ExitCustomColorMultiSelection();
        ExitPasswordTagMultiSelection();
        IsDeleteConfirmationOpen = false;
        _passwordsPendingDeletion = [];
        PasswordPendingDeletion = null;
        IsCustomColorDeleteConfirmationOpen = false;
        _customColorsPendingDeletion = [];
        CustomColorPendingDeletion = null;
        ResetPasswordTagDeleteState();
        ClearExportFlowState();
        SelectedPassword = null;
        RevealedPassword = null;
        ClearStatusMessage();
        SearchQuery = string.Empty;
        IsCreateMode = true;
        SetCustomColorPickerMode(null);
        ResetCustomColorPickerDraft();
        ResetPasswordTagEditorDraft();
        _customColorListReturnPane = EditorPane;
        CurrentPane = ListPane;
        ResetEditorFields();
    }

    public void Reset()
    {
        _token = Guid.Empty;
        _searchDebounce.Cancel();
        _customColorSearchDebounce.Cancel();
        ExitPasswordMultiSelection();
        ExitCustomColorMultiSelection();
        ExitPasswordTagMultiSelection();
        ClearPasswordItems();
        ClearPasswordTagItems();
        ClearManagedPasswordTagItems();
        _savedCustomColors.Clear();
        ClearCustomColorItems();
        Passwords = [];
        CustomColors = [];
        PasswordTags = [];
        RaisePropertiesChanged([nameof(Passwords), nameof(CustomColors), nameof(PasswordTags)]);
        RebuildPresetColors();
        RebuildPasswordTagColorOptions();
        RaisePasswordCollectionStateChanged();
        RaiseCustomColorCollectionStateChanged();
        RaisePasswordTagCollectionStateChanged();
        SelectedPassword = null;
        _passwordsPendingDeletion = [];
        PasswordPendingDeletion = null;
        _customColorsPendingDeletion = [];
        CustomColorPendingDeletion = null;
        ResetPasswordTagDeleteState();
        ClearExportFlowState();
        RevealedPassword = null;
        ClearStatusMessage();
        IsCreateMode = true;
        IsDeleteConfirmationOpen = false;
        IsCustomColorDeleteConfirmationOpen = false;
        IsPasswordTagDeleteConfirmationOpen = false;
        SearchQuery = string.Empty;
        CustomColorSearchQuery = string.Empty;
        PasswordTagSearchQuery = string.Empty;
        _searchDebounce.Cancel();
        _customColorSearchDebounce.Cancel();
        _passwordTagSearchDebounce.Cancel();
        _customColorSortKey = "name-asc";
        _passwordTagSortKey = "name-asc";
        RaiseCustomColorSortMenuLabelProperties();
        RaisePasswordTagSortMenuLabelProperties();
        SelectDefaultSortOption();
        SetCustomColorPickerMode(null);
        ResetCustomColorPickerDraft();
        ResetPasswordTagEditorDraft();
        _customColorListReturnPane = EditorPane;
        CurrentPane = ListPane;
        ResetEditorFields();
    }


    public void HideVisibleSensitiveData()
    {
        if (HasRevealedPassword)
        {
            HidePassword();
        }

        if (IsEditorPasswordVisible)
        {
            IsEditorPasswordVisible = false;
        }
    }


    public void BeginPasswordMultiSelection(PasswordItemViewModel password)
    {
        if (!IsListPaneVisible || !_allPasswords.Contains(password))
        {
            return;
        }

        if (!IsPasswordMultiSelectionActive)
        {
            IsPasswordMultiSelectionActive = true;

        }

        password.IsSelected = true;
    }

    public void BeginCustomColorMultiSelection(CustomColorItemViewModel customColor)
    {
        if (!IsCustomColorListPaneVisible || !_allCustomColors.Contains(customColor))
        {
            return;
        }

        if (!IsCustomColorMultiSelectionActive)
        {
            IsCustomColorMultiSelectionActive = true;

        }

        customColor.IsSelected = true;
    }

    private void CancelMultiSelection() => TryExitMultiSelection();

    private void SelectAllMultiSelection()
    {
        if (IsPasswordMultiSelectionActive)
        {
            var deselectAll = IsDeselectAllMultiSelectionAction;
            IEnumerable<PasswordItemViewModel> affectedPasswords = deselectAll ? _allPasswords : Passwords;

            SetSelected(affectedPasswords, !deselectAll);

            return;
        }

        if (IsCustomColorMultiSelectionActive)
        {
            var deselectAllCustomColors = IsDeselectAllMultiSelectionAction;
            IEnumerable<CustomColorItemViewModel> affectedCustomColors =
                deselectAllCustomColors ? _allCustomColors : CustomColors;

            SetSelected(affectedCustomColors, !deselectAllCustomColors);

            return;
        }

        if (!IsPasswordTagMultiSelectionActive)
        {
            return;
        }

        var deselectAllPasswordTags = IsDeselectAllMultiSelectionAction;
        IEnumerable<PasswordTagManagementItemViewModel> affectedPasswordTags =
            deselectAllPasswordTags ? _allManagedPasswordTags : PasswordTags;

        SetSelected(affectedPasswordTags, !deselectAllPasswordTags);
    }

    private void BeginExportMultiSelection()
    {
        if (!CanExportMultiSelectionItems)
        {
            return;
        }

        IReadOnlyList<Guid> selectedIds;
        if (IsPasswordMultiSelectionActive)
        {
            selectedIds = _allPasswords.Where(item => item.IsSelected).Select(item => item.Id).ToArray();
            _pendingExportKind = MultiSelectionExportKind.Passwords;
        }
        else if (IsCustomColorMultiSelectionActive)
        {
            selectedIds = _allCustomColors.Where(item => item.IsSelected).Select(item => item.Id).ToArray();
            _pendingExportKind = MultiSelectionExportKind.CustomColors;
        }
        else if (IsPasswordTagMultiSelectionActive)
        {
            selectedIds = _allManagedPasswordTags.Where(item => item.IsSelected).Select(item => item.Id).ToArray();
            _pendingExportKind = MultiSelectionExportKind.PasswordTags;
        }
        else
        {
            return;
        }

        if (selectedIds.Count == 0)
        {
            return;
        }

        _pendingExportItemIds = selectedIds;
        BuildExportTargetProfiles();
        if (ExportTargetProfiles.Count == 0)
        {
            ClearExportFlowState();
            RaiseMultiSelectionStateChanged();
            return;
        }

        this.RaisePropertyChanged(nameof(ExportTargetSubtitle));
        CurrentPane = ExportTargetPane;
    }

    private void BuildExportTargetProfiles()
    {
        foreach (var profile in ExportTargetProfiles)
            profile.Dispose();
        ExportTargetProfiles.Clear();

        foreach (var session in GetOtherActiveExportTargetSessions()
                     .OrderBy(session => session.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(session => session.Subtitle, StringComparer.CurrentCultureIgnoreCase))
        {
            var displayName = BuildExportTargetDisplayName(session);
            var subtitle = BuildExportTargetSubtitle(session, displayName);
            ExportTargetProfiles.Add(new ExportTargetProfileItemViewModel(
                session.Token,
                displayName,
                subtitle,
                SelectExportTarget));
        }

        this.RaisePropertyChanged(nameof(HasExportTargetProfiles));
        this.RaisePropertyChanged(nameof(IsExportTargetProfilesEmpty));
    }

    private IEnumerable<AuthSessionProfile> GetOtherActiveExportTargetSessions()
    {
        var sourceSession = _authSessionRegistry.GetSession(_token);
        var seenUserIds = new HashSet<Guid>();

        foreach (var session in _authSessionRegistry.ListSessions())
        {
            if (session.Token == Guid.Empty || session.Token == _token)
            {
                continue;
            }

            if (sourceSession is not null
                && sourceSession.UserId != Guid.Empty
                && session.UserId != Guid.Empty
                && session.UserId == sourceSession.UserId)
            {
                continue;
            }

            if (session.UserId != Guid.Empty && !seenUserIds.Add(session.UserId))
            {
                continue;
            }

            yield return session;
        }
    }

    private bool HasOtherActiveExportTargetAccount() => GetOtherActiveExportTargetSessions().Any();

    private static string BuildExportTargetDisplayName(AuthSessionProfile session)
    {
        if (!string.IsNullOrWhiteSpace(session.DisplayName))
        {
            return session.DisplayName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(session.Username))
        {
            return session.Username.Trim();
        }

        if (!string.IsNullOrWhiteSpace(session.Email))
        {
            return session.Email.Trim();
        }

        return session.UserId == Guid.Empty ? "—" : session.UserId.ToString();
    }

    private static string BuildExportTargetSubtitle(AuthSessionProfile session, string displayName)
    {
        if (!string.IsNullOrWhiteSpace(session.Subtitle)
            && !string.Equals(session.Subtitle.Trim(), displayName, StringComparison.OrdinalIgnoreCase))
        {
            return session.Subtitle.Trim();
        }

        if (!string.IsNullOrWhiteSpace(session.Email)
            && !string.Equals(session.Email.Trim(), displayName, StringComparison.OrdinalIgnoreCase))
        {
            return session.Email.Trim();
        }

        return string.Empty;
    }

    private void SelectExportTarget(Guid targetToken)
    {
        if (targetToken == Guid.Empty || targetToken == _token || _pendingExportItemIds.Count == 0)
        {
            return;
        }

        var target = ExportTargetProfiles.FirstOrDefault(profile => profile.Token == targetToken);
        if (target is null)
        {
            return;
        }

        _selectedExportTargetToken = targetToken;
        _selectedExportTargetDisplayName = target.DisplayName;
        DeleteOriginalOnExport = false;
        this.RaisePropertyChanged(nameof(ExportConfirmationMessage));
        IsExportConfirmationOpen = true;
    }

    private void CancelExportConfirmation()
    {
        IsExportConfirmationOpen = false;
        DeleteOriginalOnExport = false;
        _selectedExportTargetToken = Guid.Empty;
        _selectedExportTargetDisplayName = string.Empty;
        this.RaisePropertyChanged(nameof(ExportConfirmationMessage));
    }

    private async Task ConfirmExportAsync()
    {
        if (_isExporting
            || !IsExportConfirmationOpen
            || _selectedExportTargetToken == Guid.Empty
            || _pendingExportItemIds.Count == 0)
        {
            return;
        }

        var exportKind = _pendingExportKind;
        var sourcePane = exportKind switch
        {
            MultiSelectionExportKind.CustomColors => CustomColorListPane,
            MultiSelectionExportKind.PasswordTags => PasswordTagListPane,
            _ => ListPane
        };
        var deleteOriginal = DeleteOriginalOnExport;

        try
        {
            _isExporting = true;
            ClearStatusMessage();

            if (exportKind == MultiSelectionExportKind.Passwords)
            {
                await _endpoints.ExportPasswordsToUserAsync(
                    _token,
                    new ExportPasswordsToUserRequest
                    {
                        TargetToken = _selectedExportTargetToken,
                        PasswordIds = _pendingExportItemIds.ToArray(),
                        DeleteOriginal = deleteOriginal
                    });
            }
            else if (exportKind == MultiSelectionExportKind.CustomColors)
            {
                await _endpoints.ExportCustomUserColorsToUserAsync(
                    _token,
                    new ExportCustomUserColorsToUserRequest
                    {
                        TargetToken = _selectedExportTargetToken,
                        CustomUserColorIds = _pendingExportItemIds.ToArray(),
                        DeleteOriginal = deleteOriginal
                    });
            }
            else if (exportKind == MultiSelectionExportKind.PasswordTags)
            {
                await _endpoints.ExportPasswordTagsToUserAsync(
                    _token,
                    new ExportPasswordTagsToUserRequest
                    {
                        TargetToken = _selectedExportTargetToken,
                        PasswordTagIds = _pendingExportItemIds.ToArray(),
                        DeleteOriginal = deleteOriginal
                    });
            }
            else
            {
                return;
            }

            IsExportConfirmationOpen = false;
            ClearExportFlowState();
            SetCurrentPane(sourcePane, true);

            if (deleteOriginal && !await RefreshAsync(false))
            {
                return;
            }

            ShowSuccessMessage(GetTranslation((exportKind, deleteOriginal) switch
            {
                (MultiSelectionExportKind.Passwords, false) => "Passwords_Export_Copy_Passwords_Success",
                (MultiSelectionExportKind.Passwords, true) => "Passwords_Export_Move_Passwords_Success",
                (MultiSelectionExportKind.CustomColors, false) => "Passwords_Export_Copy_CustomColors_Success",
                (MultiSelectionExportKind.CustomColors, true) => "Passwords_Export_Move_CustomColors_Success",
                (MultiSelectionExportKind.PasswordTags, false) => "Passwords_Export_Copy_Tags_Success",
                _ => "Passwords_Export_Move_Tags_Success"
            }));
        }
        catch (Exception ex)
        {
            CancelExportConfirmation();
            ShowErrorMessage(GetSafeErrorMessage(ex));
        }
        finally
        {
            _isExporting = false;
        }
    }

    private void BackFromExportTarget()
    {
        if (_pendingExportKind == MultiSelectionExportKind.None)
        {
            SetCurrentPane(ListPane, true);
            return;
        }

        CancelExportConfirmation();
        var exportKind = _pendingExportKind;
        var selectedIds = _pendingExportItemIds.ToHashSet();
        var sourcePane = exportKind switch
        {
            MultiSelectionExportKind.CustomColors => CustomColorListPane,
            MultiSelectionExportKind.PasswordTags => PasswordTagListPane,
            _ => ListPane
        };
        ClearExportFlowState();
        SetCurrentPane(sourcePane, true);

        if (exportKind == MultiSelectionExportKind.Passwords)
        {
            IsPasswordMultiSelectionActive = true;

            foreach (var item in _allPasswords)
            {
                item.IsSelected = selectedIds.Contains(item.Id);
            }
        }
        else if (exportKind == MultiSelectionExportKind.CustomColors)
        {
            IsCustomColorMultiSelectionActive = true;

            foreach (var item in _allCustomColors)
            {
                item.IsSelected = selectedIds.Contains(item.Id);
            }
        }
        else
        {
            IsPasswordTagMultiSelectionActive = true;

            foreach (var item in _allManagedPasswordTags)
            {
                item.IsSelected = selectedIds.Contains(item.Id);
            }
        }

        RaiseMultiSelectionStateChanged();
    }

    private void ClearExportFlowState()
    {
        IsExportConfirmationOpen = false;
        DeleteOriginalOnExport = false;
        _pendingExportKind = MultiSelectionExportKind.None;
        _pendingExportItemIds = [];
        _selectedExportTargetToken = Guid.Empty;
        _selectedExportTargetDisplayName = string.Empty;
        foreach (var profile in ExportTargetProfiles)
            profile.Dispose();
        ExportTargetProfiles.Clear();
        this.RaisePropertyChanged(nameof(HasExportTargetProfiles));
        this.RaisePropertyChanged(nameof(IsExportTargetProfilesEmpty));
        this.RaisePropertyChanged(nameof(ExportTargetSubtitle));
        this.RaisePropertyChanged(nameof(ExportConfirmationMessage));
    }

    private void BeginDeleteMultiSelection()
    {
        if (IsPasswordMultiSelectionActive)
        {
            var selectedPasswords = _allPasswords.Where(item => item.IsSelected).ToList();
            if (selectedPasswords.Count == 0)
            {
                return;
            }

            BeginDeletePasswords(selectedPasswords);
            return;
        }

        if (IsCustomColorMultiSelectionActive)
        {
            var selectedCustomColors = _allCustomColors.Where(item => item.IsSelected).ToList();
            if (selectedCustomColors.Count == 0)
            {
                return;
            }

            BeginDeleteCustomColors(selectedCustomColors);
            return;
        }

        if (!IsPasswordTagMultiSelectionActive)
        {
            return;
        }

        var selectedTags = _allManagedPasswordTags.Where(item => item.IsSelected).ToList();
        if (selectedTags.Count == 0)
        {
            return;
        }

        BeginDeletePasswordTags(selectedTags);
    }

    private void ExitPasswordMultiSelection()
    {
        IsPasswordMultiSelectionActive = false;
        SetSelected(_allPasswords, false);
    }

    private void ExitCustomColorMultiSelection()
    {
        IsCustomColorMultiSelectionActive = false;
        SetSelected(_allCustomColors, false);
    }

    private int _selectionBatchDepth;

    private void SetSelected<TItem>(IEnumerable<TItem> items, bool selected)
        where TItem : MultiSelectableListItemViewModel
    {
        _selectionBatchDepth++;
        try
        {
            foreach (var item in items)
                item.IsSelected = selected;
        }
        finally
        {
            _selectionBatchDepth--;
            RaiseMultiSelectionStateChanged();
        }
    }

    private void HandlePasswordItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MultiSelectableListItemViewModel.IsSelected))
        {
            RaiseMultiSelectionStateChanged();
        }
    }

    private void HandleCustomColorItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MultiSelectableListItemViewModel.IsSelected))
        {
            RaiseMultiSelectionStateChanged();
        }
    }

    private void RaiseMultiSelectionStateChanged()
    {
        if (_selectionBatchDepth > 0)
            return;
        this.RaisePropertyChanged(nameof(IsMultiSelectionToolbarVisible));
        this.RaisePropertyChanged(nameof(HasSelectedMultiSelectionItems));
        this.RaisePropertyChanged(nameof(AreAllVisibleMultiSelectionItemsSelected));
        this.RaisePropertyChanged(nameof(IsSelectAllMultiSelectionAction));
        this.RaisePropertyChanged(nameof(IsDeselectAllMultiSelectionAction));
        this.RaisePropertyChanged(nameof(MultiSelectionSelectAllLabel));
        this.RaisePropertyChanged(nameof(CanExportMultiSelectionItems));
        this.RaisePropertyChanged(nameof(MultiSelectionExportToolTip));
        this.RaisePropertyChanged(nameof(MultiSelectionDeleteToolTip));
    }

    public bool TryExitMultiSelection()
    {
        if (IsPasswordTagMultiSelectionActive)
        {
            ExitPasswordTagMultiSelection();
            return true;
        }

        if (IsCustomColorMultiSelectionActive)
        {
            ExitCustomColorMultiSelection();
            return true;
        }

        if (IsPasswordMultiSelectionActive)
        {
            ExitPasswordMultiSelection();
            return true;
        }

        return false;
    }


    internal bool HasConfirmableDialogOpen =>
        IsPasswordTagDeleteConfirmationOpen
        || IsCustomColorDeleteConfirmationOpen
        || IsDeleteConfirmationOpen
        || IsExportConfirmationOpen;

    internal async Task ConfirmOpenDialogAsync()
    {
        if (IsExportConfirmationOpen)
        {
            await ConfirmExportAsync();
            return;
        }

        if (IsPasswordTagDeleteConfirmationOpen)
        {
            await ConfirmDeletePasswordTagAsync();
            return;
        }

        if (IsCustomColorDeleteConfirmationOpen)
        {
            await ConfirmDeleteCustomColorAsync();
            return;
        }

        if (IsDeleteConfirmationOpen)
            await ConfirmDeletePasswordAsync();
    }


    public bool TryNavigateBack()
    {
        if (IsExportConfirmationOpen)
        {
            CancelExportConfirmation();
            return true;
        }

        if (IsPasswordTagDeleteConfirmationOpen)
        {
            CancelDeletePasswordTag();
            return true;
        }

        if (IsCustomColorDeleteConfirmationOpen)
        {
            CancelDeleteCustomColor();
            return true;
        }

        if (IsDeleteConfirmationOpen)
        {
            CancelDeletePassword();
            return true;
        }

        if (TryExitMultiSelection())
            return true;

        if (IsExportTargetPaneVisible)
        {
            BackFromExportTarget();
            return true;
        }

        if (IsColorPaneVisible)
        {
            BackFromColorPicker();
            return true;
        }

        if (IsCustomColorListPaneVisible)
        {
            BackFromCustomColorList();
            return true;
        }

        if (IsPasswordTagEditorPaneVisible)
        {
            BackFromPasswordTagEditor();
            return true;
        }

        if (IsPasswordTagListPaneVisible)
        {
            BackFromPasswordTagList();
            return true;
        }

        if (IsEditorPaneVisible)
        {
            CancelPasswordEditor();
            return true;
        }

        if (IsDetailsPaneVisible)
        {
            BackToList();
            return true;
        }

        return false;
    }


    private async Task ExecutePrimaryActionAsync()
    {
        if (IsExportConfirmationOpen)
        {
            await ConfirmExportAsync();
            return;
        }

        if (IsPasswordTagDeleteConfirmationOpen)
        {
            await ConfirmDeletePasswordTagAsync();
            return;
        }

        if (IsCustomColorDeleteConfirmationOpen)
        {
            await ConfirmDeleteCustomColorAsync();
            return;
        }

        if (IsDeleteConfirmationOpen)
        {
            await ConfirmDeletePasswordAsync();
            return;
        }

        if (IsEditorPaneVisible)
        {
            await SavePasswordAsync();
            return;
        }

        if (IsColorPaneVisible)
        {
            await SaveCustomColorAsync();
            return;
        }

        if (IsCustomColorListPaneVisible)
        {
            ApplyCustomColorFiltersAndSorting();
            return;
        }

        if (IsPasswordTagEditorPaneVisible)
        {
            await SavePasswordTagAsync();
            return;
        }

        if (IsPasswordTagListPaneVisible)
        {
            ApplyPasswordTagFiltersAndSorting();
            return;
        }

        if (IsListPaneVisible)
        {
            ApplyCurrentSearch();
        }
    }


    private async Task<bool> RefreshAsync(bool showSuccessMessage)
    {
        if (_token == Guid.Empty)
        {
            return false;
        }

        ClearStatusMessage();
        ExitPasswordMultiSelection();
        ExitCustomColorMultiSelection();
        ExitPasswordTagMultiSelection();
        var selectedId = SelectedPassword?.Id;

        try
        {
            var response = await _endpoints.GetSavedPasswordsAsync(_token);
            var selectedEditorTagIds = EditorSelectedTags.Select(tag => tag.Id).ToArray();
            RebuildPasswordTagItems(response.Tags, selectedEditorTagIds);
            RebuildManagedPasswordTagItems(response.Tags);
            var tagNameById = _allPasswordTags.ToDictionary(tag => tag.Id, tag => tag.Name);
            var currentEditorColor = EditorColor;
            var currentPasswordTagEditorColor = PasswordTagEditorColor;

            _savedCustomColors.Clear();
            _savedCustomColors.AddRange(response.CustomColors);
            RebuildPresetColors();
            RebuildPasswordTagColorOptions();
            RebuildCustomColorItems();
            ApplyEditorColor(currentEditorColor);
            ApplyPasswordTagEditorColor(currentPasswordTagEditorColor);

            var colorNameByCode = BuildColorNameByCodeLookup(response.CustomColors);

            ClearPasswordItems();

            foreach (var password in response.Passwords)
            {
                var tagNames = password.TagIds
                    .Select(tagId => tagNameById.TryGetValue(tagId, out var tagName) ? tagName : null)
                    .Where(tagName => !string.IsNullOrWhiteSpace(tagName))
                    .Select(tagName => tagName!)
                    .ToList();

                var normalizedPasswordColor = PasswordColorUtility.NormalizeKnownColor(password.Color);
                colorNameByCode.TryGetValue(normalizedPasswordColor, out var colorName);

                var passwordItem = PasswordItemViewModel.Create(
                    password,
                    tagNames,
                    colorName,
                    EditPasswordLabel,
                    DeletePasswordLabel,
                    this);

                passwordItem.PropertyChanged += HandlePasswordItemPropertyChanged;
                _allPasswords.Add(passwordItem);
            }

            ApplyFiltersAndSorting(selectedId, preserveSelection: selectedId.HasValue);
            if (showSuccessMessage)
                ShowSuccessMessage(GetTranslation("Passwords_Refreshed"));

            return true;
        }
        catch (Exception ex)
        {
            ShowErrorMessage(GetSafeErrorMessage(ex));
            return false;
        }
    }

    private Dictionary<string, string> BuildColorNameByCodeLookup(
        IEnumerable<CustomUserColorInfoResponse> customColors)
    {
        var namesByCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var customColor in customColors)
        {
            if (string.IsNullOrWhiteSpace(customColor.ColorName))
            {
                continue;
            }

            var normalizedColorCode = PasswordColorUtility.NormalizeKnownColor(customColor.ColorCode);
            namesByCode[normalizedColorCode] = customColor.ColorName.Trim();
        }

        namesByCode[PasswordColorUtility.DefaultColor] = GetTranslation("Passwords_Color_Teal");

        return namesByCode;
    }

    private void ApplyCurrentSearch()
    {
        _searchDebounce.Cancel();
        ApplyFiltersAndSorting(SelectedPassword?.Id, preserveSelection: true);
    }

    private Task BeginViewPasswordAsync(PasswordItemViewModel password)
    {
        SelectedPassword = password;
        HidePassword();
        ClearStatusMessage();
        CurrentPane = DetailsPane;
        return Task.CompletedTask;
    }

    private void BeginCreatePassword()
    {
        IsCreateMode = true;
        ClearStatusMessage();
        SelectedPassword = null;
        HidePassword();
        ResetEditorFields();
        CurrentPane = EditorPane;
    }

    private async Task EditSelectedPasswordAsync()
    {
        if (SelectedPassword is null)
        {
            return;
        }

        await BeginEditPasswordAsync(SelectedPassword);
    }

    private Task BeginEditPasswordAsync(PasswordItemViewModel password)
    {
        SelectedPassword = password;
        IsCreateMode = false;
        ClearStatusMessage();
        EditorName = password.Name;
        EditorDescription = password.Description;
        EditorPassword = string.Empty;
        IsEditorPasswordVisible = false;
        IsEditorStoredPasswordRevealed = false;
        SetEditorSelectedTags(password.TagIds);
        ApplyEditorColor(password.Color);
        CurrentPane = EditorPane;
        return Task.CompletedTask;
    }

    private void SelectEditorTag(PasswordTagItemViewModel tag)
    {
        if (!_allPasswordTags.Contains(tag) || EditorSelectedTags.Any(selectedTag => selectedTag.Id == tag.Id))
        {
            return;
        }

        EditorSelectedTags.Add(tag);
        SortEditorSelectedTags();

        if (!string.IsNullOrEmpty(EditorTagSearchQuery))
        {
            EditorTagSearchQuery = string.Empty;
        }
        else
        {
            RefreshEditorTagSuggestions();
        }

        // A newly selected tag becomes the password's suggested visual color.
        // The user can still choose any other password color afterwards.
        ApplyEditorColor(tag.Color);
    }

    private void RemoveEditorTag(PasswordTagItemViewModel tag)
    {
        if (!_allPasswordTags.Contains(tag))
            return;
        var selectedTag = EditorSelectedTags.FirstOrDefault(item => item.Id == tag.Id);
        if (selectedTag is null)
        {
            return;
        }

        EditorSelectedTags.Remove(selectedTag);
        RefreshEditorTagSuggestions();
    }

    private void RebuildPasswordTagItems(
        IReadOnlyList<PasswordTagInfoResponse> tags,
        IReadOnlyCollection<Guid> selectedTagIds)
    {
        _allPasswordTags.Clear();

        foreach (var tag in tags.OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            _allPasswordTags.Add(PasswordTagItemViewModel.Create(
                tag,
                GetEditorRemoveTagLabel(tag.Name),
                this));
        }

        SetEditorSelectedTags(selectedTagIds);
        RefreshSelectedPasswordTags();
    }

    private void ClearPasswordTagItems()
    {
        _allPasswordTags.Clear();
        RefreshSelectedPasswordTags();
        ClearEditorTagSelection();
        RaiseEditorTagStateChanged();
    }

    private void RefreshSelectedPasswordTags()
    {
        SelectedPasswordTags.Clear();

        if (SelectedPassword is not null)
        {
            var selectedTagIds = SelectedPassword.TagIds.ToHashSet();
            foreach (var tag in _allPasswordTags.Where(tag => selectedTagIds.Contains(tag.Id)))
            {
                SelectedPasswordTags.Add(tag);
            }
        }

        this.RaisePropertyChanged(nameof(HasSelectedPasswordTags));
        this.RaisePropertyChanged(nameof(HasNoSelectedPasswordTags));
    }

    private void SetEditorSelectedTags(IEnumerable<Guid> tagIds)
    {
        var selectedTagIds = tagIds.ToHashSet();

        EditorSelectedTags.Clear();
        foreach (var tag in _allPasswordTags.Where(tag => selectedTagIds.Contains(tag.Id)))
        {
            EditorSelectedTags.Add(tag);
        }

        _editorTagSearchQuery = string.Empty;
        this.RaisePropertyChanged(nameof(EditorTagSearchQuery));
        RefreshEditorTagSuggestions();
    }

    private void ClearEditorTagSelection()
    {
        EditorSelectedTags.Clear();
        _editorTagSearchQuery = string.Empty;
        _isEditorTagSearchFocused = false;
        this.RaisePropertyChanged(nameof(EditorTagSearchQuery));
        this.RaisePropertyChanged(nameof(IsEditorTagSearchFocused));
        RefreshEditorTagSuggestions();
    }

    private void SortEditorSelectedTags()
    {
        var sortedTags = EditorSelectedTags
            .OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        if (EditorSelectedTags.SequenceEqual(sortedTags))
        {
            return;
        }

        EditorSelectedTags.Clear();
        foreach (var tag in sortedTags)
        {
            EditorSelectedTags.Add(tag);
        }
    }

    private void RefreshEditorTagSuggestions()
    {
        var selectedTagIds = EditorSelectedTags.Select(tag => tag.Id).ToHashSet();
        var searchTerm = EditorTagSearchQuery.Trim();

        var suggestions = _allPasswordTags
            .Where(tag => !selectedTagIds.Contains(tag.Id))
            .Where(tag => string.IsNullOrWhiteSpace(searchTerm)
                || tag.Name.Contains(searchTerm, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaximumVisibleEditorTagSuggestions)
            .ToArray();

        EditorTagSuggestions.Clear();
        foreach (var tag in suggestions)
        {
            EditorTagSuggestions.Add(tag);
        }

        RaiseEditorTagStateChanged();
    }

    private void RaiseEditorTagStateChanged()
    {
        this.RaisePropertyChanged(nameof(HasAvailablePasswordTags));
        this.RaisePropertyChanged(nameof(HasSelectedEditorTags));
        this.RaisePropertyChanged(nameof(AreAllEditorTagsSelected));
        this.RaisePropertyChanged(nameof(IsEditorTagSearchEnabled));
        this.RaisePropertyChanged(nameof(IsEditorTagSuggestionPanelVisible));
        this.RaisePropertyChanged(nameof(HasEditorTagSuggestions));
        this.RaisePropertyChanged(nameof(IsEditorTagNoMatchesVisible));
        this.RaisePropertyChanged(nameof(IsEditorTagEmptyStateVisible));
        this.RaisePropertyChanged(nameof(IsEditorAllTagsSelectedVisible));
    }

    private List<Guid> GetSelectedEditorTagIds() =>
        EditorSelectedTags.Select(tag => tag.Id).Order().ToList();

    private string GetEditorRemoveTagLabel(string tagName) =>
        string.Format(GetTranslation("Passwords_Editor_RemoveTag"), tagName);

    private void BeginDeleteSelectedPassword()
    {
        if (SelectedPassword is null)
        {
            return;
        }

        BeginDeletePassword(SelectedPassword);
    }

    private Task BeginDeletePasswordAsync(PasswordItemViewModel password)
    {
        BeginDeletePasswords([password]);
        return Task.CompletedTask;
    }

    private void BeginDeletePassword(PasswordItemViewModel password)
    {
        BeginDeletePasswords([password]);
    }

    private void BeginDeletePasswords(IReadOnlyList<PasswordItemViewModel> passwords)
    {
        if (passwords.Count == 0)
        {
            return;
        }

        ClearStatusMessage();
        _passwordsPendingDeletion = passwords.ToArray();
        PasswordPendingDeletion = _passwordsPendingDeletion[0];
        IsDeleteConfirmationOpen = true;
    }

    private void CancelDeletePassword()
    {
        ClearStatusMessage();
        IsDeleteConfirmationOpen = false;
        _passwordsPendingDeletion = [];
        PasswordPendingDeletion = null;
    }

    private async Task ConfirmDeletePasswordAsync()
    {
        if (_isDeletingPassword || _passwordsPendingDeletion.Count == 0)
        {
            return;
        }

        ClearStatusMessage();
        var passwords = _passwordsPendingDeletion.ToArray();
        var passwordIds = passwords.Select(item => item.Id).ToArray();

        try
        {
            _isDeletingPassword = true;
            await _endpoints.RemovePasswordsAsync(_token, passwordIds);
            CancelDeletePassword();

            if (SelectedPassword is not null && passwordIds.Contains(SelectedPassword.Id))
            {
                SelectedPassword = null;
                HidePassword();
                CurrentPane = ListPane;
            }

            if (!await RefreshAsync(false))
                return;

            ShowSuccessMessage(GetTranslation(
                passwords.Length > 1
                    ? "Passwords_Delete_MultipleSuccess"
                    : "Passwords_Delete_Success"));
        }
        catch (Exception ex)
        {
            ShowErrorMessage(GetSafeErrorMessage(ex));
        }
        finally
        {
            _isDeletingPassword = false;
        }
    }

    private async Task RevealPasswordAsync()
    {
        if (SelectedPassword is null || HasRevealedPassword)
        {
            return;
        }

        ClearStatusMessage();

        try
        {
            var passwordBytes = await _endpoints.GetUnsecurePasswordAsync(_token, SelectedPassword.Id);
            try
            {
                RevealedPassword = Encoding.UTF8.GetString(passwordBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }
        }
        catch (Exception ex)
        {
            ShowErrorMessage(GetSafeErrorMessage(ex));
        }
    }

    private void HidePassword()
    {
        RevealedPassword = null;
    }

    private async Task CopyRevealedPasswordAsync()
    {
        if (!HasRevealedPassword)
        {
            return;
        }

        ClearStatusMessage();

        try
        {
            if (await TryCopyTextToClipboardAsync(RevealedPassword))
                ShowSuccessMessage(GetTranslation("Passwords_Copy_Success"));
            else
                ShowErrorMessage(GetTranslation("Error_ClipboardUnavailable"));
        }
        catch
        {
            ShowErrorMessage(GetTranslation("Error_ClipboardUnavailable"));
        }
    }


    private async Task RevealEditorPasswordAsync()
    {
        if (SelectedPassword is null || IsCreateMode)
        {
            return;
        }

        ClearStatusMessage();

        try
        {
            var passwordBytes = await _endpoints.GetUnsecurePasswordAsync(_token, SelectedPassword.Id);
            try
            {
                EditorPassword = Encoding.UTF8.GetString(passwordBytes);
                IsEditorPasswordVisible = true;
                IsEditorStoredPasswordRevealed = true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }
        }
        catch (Exception ex)
        {
            ShowErrorMessage(GetSafeErrorMessage(ex));
        }
    }

    private async Task SavePasswordAsync()
    {
        if (_isSavingPassword || !ValidatePasswordEditor())
            return;

        try
        {
            _isSavingPassword = true;
            var successMessage = await PersistPasswordEditorAsync();
            if (successMessage is null)
                return;

            HidePassword();
            ResetEditorFields();
            CurrentPane = ListPane;
            if (await RefreshAsync(false))
                ShowSuccessMessage(successMessage);
        }
        catch (Exception ex)
        {
            ShowErrorMessage(GetSafeErrorMessage(ex));
        }
        finally
        {
            _isSavingPassword = false;
        }
    }

    private bool ValidatePasswordEditor()
    {
        ClearStatusMessage();
        if (string.IsNullOrWhiteSpace(EditorName))
        {
            ShowErrorMessage(GetTranslation("Validation_PasswordName_Required"));
            return false;
        }

        if (IsCreateMode && string.IsNullOrWhiteSpace(EditorPassword))
        {
            ShowErrorMessage(GetTranslation("Validation_RegisterPassword_Required"));
            return false;
        }

        return true;
    }

    private async Task<string?> PersistPasswordEditorAsync()
    {
        if (IsCreateMode)
        {
            await CreatePasswordFromEditorAsync();
            return GetTranslation("Passwords_Save_CreateSuccess");
        }

        if (SelectedPassword is null)
            return null;

        await UpdatePasswordFromEditorAsync(SelectedPassword.Id);
        return GetTranslation("Passwords_Save_UpdateSuccess");
    }

    private async Task CreatePasswordFromEditorAsync()
    {
        var rawPassword = SecretTransform.Utf8Bytes(EditorPassword);
        try
        {
            await _endpoints.AddNewPasswordAsync(_token, new NewPasswordRequest
            {
                Name = EditorName.Trim(),
                Description = EditorDescription.Trim(),
                Color = EditorColor,
                Password = rawPassword,
                TagIds = GetSelectedEditorTagIds()
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawPassword);
        }
    }

    private async Task UpdatePasswordFromEditorAsync(Guid passwordId)
    {
        byte[]? rawPassword = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(EditorPassword))
                rawPassword = SecretTransform.Utf8Bytes(EditorPassword);

            await _endpoints.UpdatePasswordAsync(_token, new UpdatePasswordRequest
            {
                Id = passwordId,
                Name = EditorName.Trim(),
                Description = EditorDescription.Trim(),
                Color = EditorColor,
                Password = rawPassword,
                TagIds = GetSelectedEditorTagIds()
            });
        }
        finally
        {
            if (rawPassword is not null)
                CryptographicOperations.ZeroMemory(rawPassword);
        }
    }

    private void CancelPasswordEditor()
    {
        ResetEditorFields();
        HidePassword();
        ClearStatusMessage();
        SetCurrentPane(ListPane, true);
    }

    private void RefreshEditorPasswordStrength()
    {
        var result = _passwordStrengthEstimator.Evaluate(
            EditorPassword,
            [EditorName]);

        if (_editorPasswordStrength == result.Score)
            return;

        _editorPasswordStrength = result.Score;
        this.RaisePropertyChanged(nameof(EditorPasswordStrength));
    }

    private void RefreshRevealedPasswordStrength()
    {
        var result = _passwordStrengthEstimator.Evaluate(
            RevealedPassword,
            [SelectedPassword?.Name]);

        if (_revealedPasswordStrength == result.Score)
            return;

        _revealedPasswordStrength = result.Score;
        this.RaisePropertyChanged(nameof(RevealedPasswordStrength));
    }

    private void GenerateEditorPassword()
    {
        EditorPassword = _passwordGenerator.Generate([EditorName]);
        IsEditorPasswordVisible = true;
    }

    private void ToggleEditorPasswordVisibility() => IsEditorPasswordVisible = !IsEditorPasswordVisible;

    private void BackToList()
    {
        HidePassword();
        ResetEditorFields();
        ClearStatusMessage();
        SetCurrentPane(ListPane, true);
    }

    private void ResetEditorFields()
    {
        EditorName = string.Empty;
        EditorDescription = string.Empty;
        ClearEditorTagSelection();
        ApplyEditorColor(PasswordColorUtility.DefaultColor);
        EditorPassword = string.Empty;
        IsEditorPasswordVisible = false;
        IsEditorStoredPasswordRevealed = false;
    }

    private void ApplyFiltersAndSorting(Guid? preferredSelectionId, bool preserveSelection)
    {
        _searchDebounce.Cancel();
        IEnumerable<PasswordItemViewModel> query = _allPasswords;

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var searchTerm = SearchQuery.Trim();
            query = query.Where(item =>
                IsPasswordSearchNameEnabled && item.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
                || IsPasswordSearchDescriptionEnabled && item.Description.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
                || IsPasswordSearchTagEnabled && item.HasTagMatching(searchTerm));
        }

        query = SelectedSortOption?.Key switch
        {
            "name-desc" => query.OrderByDescending(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            "created-desc" => query.OrderByDescending(item => item.CreatedAt),
            "created-asc" => query.OrderBy(item => item.CreatedAt),
            "updated-asc" => query.OrderBy(item => item.LastUpdatedAt),
            "updated-desc" => query.OrderByDescending(item => item.LastUpdatedAt),
            _ => query.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
        };

        var filtered = query.ToList();
        Passwords = new ObservableCollection<PasswordItemViewModel>(filtered);
        this.RaisePropertyChanged(nameof(Passwords));

        RaisePasswordCollectionStateChanged();

        if (preserveSelection && preferredSelectionId.HasValue)
        {
            SelectedPassword = filtered.FirstOrDefault(item => item.Id == preferredSelectionId.Value);
        }
        else
        {
            SelectedPassword = null;
        }
    }

    private void RaisePasswordCollectionStateChanged()
    {
        this.RaisePropertyChanged(nameof(HasPasswords));
        this.RaisePropertyChanged(nameof(IsEmpty));
        this.RaisePropertyChanged(nameof(HasStoredPasswords));
        this.RaisePropertyChanged(nameof(IsVaultEmpty));
        this.RaisePropertyChanged(nameof(IsSearchResultEmpty));
        RaiseMultiSelectionStateChanged();
    }

    private void ClearPasswordItems()
    {
        foreach (var password in _allPasswords)
        {
            password.PropertyChanged -= HandlePasswordItemPropertyChanged;
        }

        _allPasswords.Clear();
        RaiseMultiSelectionStateChanged();
    }

    private void RebuildCustomColorItems()
    {
        ClearCustomColorItems();

        foreach (var customColor in _savedCustomColors)
        {
            var normalizedColor = PasswordColorUtility.NormalizeKnownColor(customColor.ColorCode);
            if (string.Equals(normalizedColor, PasswordColorUtility.DefaultColor, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var customColorItem = CustomColorItemViewModel.Create(
                customColor,
                DeletePasswordLabel,
                this);

            customColorItem.PropertyChanged += HandleCustomColorItemPropertyChanged;
            _allCustomColors.Add(customColorItem);
        }

        ApplyCustomColorFiltersAndSorting();
    }

    private void ApplyCustomColorFiltersAndSorting()
    {
        _customColorSearchDebounce.Cancel();
        IEnumerable<CustomColorItemViewModel> query = _allCustomColors;

        if (!string.IsNullOrWhiteSpace(CustomColorSearchQuery))
        {
            var searchTerm = CustomColorSearchQuery.Trim();
            query = query.Where(item =>
                (IsCustomColorSearchNameEnabled
                    && !string.IsNullOrWhiteSpace(item.Name)
                    && item.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                || (IsCustomColorSearchCodeEnabled
                    && item.ColorCode.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)));
        }

        query = _customColorSortKey switch
        {
            "name-desc" => query.OrderByDescending(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            _ => query.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        };

        CustomColors = new ObservableCollection<CustomColorItemViewModel>(query);
        this.RaisePropertyChanged(nameof(CustomColors));

        RaiseCustomColorCollectionStateChanged();
    }

    private void RaiseCustomColorCollectionStateChanged()
    {
        this.RaisePropertyChanged(nameof(HasCustomColors));
        this.RaisePropertyChanged(nameof(HasStoredCustomColors));
        this.RaisePropertyChanged(nameof(IsCustomColorListEmpty));
        this.RaisePropertyChanged(nameof(IsCustomColorSearchResultEmpty));
        RaiseMultiSelectionStateChanged();
    }

    private void ClearCustomColorItems()
    {
        foreach (var customColor in _allCustomColors)
        {
            customColor.PropertyChanged -= HandleCustomColorItemPropertyChanged;
        }

        _allCustomColors.Clear();
        RaiseMultiSelectionStateChanged();
    }

    private void RebuildPresetColors()
    {
        PresetColors.Clear();
        PresetColors.Add(new PasswordColorOptionViewModel("teal", GetTranslation("Passwords_Color_Teal"), PasswordColorUtility.DefaultColor));

        foreach (var customColor in _savedCustomColors)
        {
            var displayName = string.IsNullOrWhiteSpace(customColor.ColorName)
                ? customColor.ColorCode
                : customColor.ColorName;
            PresetColors.Add(new PasswordColorOptionViewModel($"saved-custom:{customColor.Id:N}", displayName, customColor.ColorCode));
        }

        PresetColors.Add(new PasswordColorOptionViewModel(CustomColorKey, GetTranslation("Passwords_Color_More"), EditorColor, isManageColorsOption: true));
    }

    private void RebuildSortOptions()
    {
        SortOptions.Clear();
        SortOptions.Add(new PasswordSortOptionViewModel("name-asc", GetTranslation("Passwords_Sort_NameAsc")));
        SortOptions.Add(new PasswordSortOptionViewModel("name-desc", GetTranslation("Passwords_Sort_NameDesc")));
        SortOptions.Add(new PasswordSortOptionViewModel("created-desc", GetTranslation("Passwords_Sort_CreatedNewest")));
        SortOptions.Add(new PasswordSortOptionViewModel("created-asc", GetTranslation("Passwords_Sort_CreatedOldest")));
        SortOptions.Add(new PasswordSortOptionViewModel("updated-desc", GetTranslation("Passwords_Sort_UpdatedNewest")));
        SortOptions.Add(new PasswordSortOptionViewModel("updated-asc", GetTranslation("Passwords_Sort_UpdatedOldest")));
    }

    private void SelectDefaultPresetColor() => ApplyEditorColor(PasswordColorUtility.DefaultColor);

    private void SelectDefaultSortOption() => SelectedSortOption = SortOptions.FirstOrDefault(item => item.Key == "name-asc") ?? SortOptions.FirstOrDefault();

    private void SetPasswordSearchMode(ref bool field, bool value, string propertyName)
    {
        if (field == value)
        {
            return;
        }

        if (!value && EnabledPasswordSearchModeCount <= 1)
        {
            this.RaisePropertyChanged(propertyName);
            RaisePasswordSearchModeToggleProperties();
            return;
        }

        this.RaiseAndSetIfChanged(ref field, value, propertyName);
        RaisePasswordSearchModeToggleProperties();
        ApplyFiltersAndSorting(SelectedPassword?.Id, preserveSelection: true);
    }

    private bool CanTogglePasswordSearchMode(bool isEnabled) => !isEnabled || EnabledPasswordSearchModeCount > 1;

    private void RaisePasswordSearchModeToggleProperties()
    {
        this.RaisePropertyChanged(nameof(CanTogglePasswordSearchName));
        this.RaisePropertyChanged(nameof(CanTogglePasswordSearchDescription));
        this.RaisePropertyChanged(nameof(CanTogglePasswordSearchTag));
    }

    private int EnabledPasswordSearchModeCount =>
        (_isPasswordSearchNameEnabled ? 1 : 0)
        + (_isPasswordSearchDescriptionEnabled ? 1 : 0)
        + (_isPasswordSearchTagEnabled ? 1 : 0);

    private void SetCustomColorSearchMode(ref bool field, bool value, string propertyName)
    {
        if (field == value)
        {
            return;
        }

        if (!value && EnabledCustomColorSearchModeCount <= 1)
        {
            this.RaisePropertyChanged(propertyName);
            RaiseCustomColorSearchModeToggleProperties();
            return;
        }

        this.RaiseAndSetIfChanged(ref field, value, propertyName);
        RaiseCustomColorSearchModeToggleProperties();
        ApplyCustomColorFiltersAndSorting();
    }

    private bool CanToggleCustomColorSearchMode(bool isEnabled) => !isEnabled || EnabledCustomColorSearchModeCount > 1;

    private void RaiseCustomColorSearchModeToggleProperties()
    {
        this.RaisePropertyChanged(nameof(CanToggleCustomColorSearchName));
        this.RaisePropertyChanged(nameof(CanToggleCustomColorSearchCode));
    }

    private int EnabledCustomColorSearchModeCount =>
        (_isCustomColorSearchNameEnabled ? 1 : 0)
        + (_isCustomColorSearchCodeEnabled ? 1 : 0);

    private void UpdateSortOptionSelectionMarks()
    {
        foreach (var option in SortOptions)
            option.IsSelected = ReferenceEquals(option, SelectedSortOption);
    }

    private void SelectSortOptionByKey(string key)
    {
        var option = SortOptions.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));

        if (option is not null)
            SelectedSortOption = option;
    }

    private string BuildSortMenuLabel(string key, string translationKey) =>
        $"{(string.Equals(SelectedSortOption?.Key, key, StringComparison.Ordinal) ? "✓ " : "   ")}{GetTranslation(translationKey)}";

    private void RaiseSortMenuLabelProperties()
    {
        this.RaisePropertyChanged(nameof(SortNameAscMenuLabel));
        this.RaisePropertyChanged(nameof(SortNameDescMenuLabel));
        this.RaisePropertyChanged(nameof(SortCreatedNewestMenuLabel));
        this.RaisePropertyChanged(nameof(SortCreatedOldestMenuLabel));
        this.RaisePropertyChanged(nameof(SortUpdatedNewestMenuLabel));
        this.RaisePropertyChanged(nameof(SortUpdatedOldestMenuLabel));
    }

    private void SelectCustomColorSortOption(string key)
    {
        if (key is not ("name-asc" or "name-desc")
            || string.Equals(_customColorSortKey, key, StringComparison.Ordinal))
        {
            return;
        }

        _customColorSortKey = key;
        RaiseCustomColorSortMenuLabelProperties();
        ApplyCustomColorFiltersAndSorting();
    }

    private string BuildCustomColorSortMenuLabel(string key, string translationKey) =>
        $"{(string.Equals(_customColorSortKey, key, StringComparison.Ordinal) ? "✓ " : "   ")}{GetTranslation(translationKey)}";

    private void RaiseCustomColorSortMenuLabelProperties()
    {
        this.RaisePropertyChanged(nameof(CustomColorsSortNameAscMenuLabel));
        this.RaisePropertyChanged(nameof(CustomColorsSortNameDescMenuLabel));
    }

    private void OpenCustomColorList(string returnPane = EditorPane)
    {
        _customColorListReturnPane = returnPane;
        this.RaisePropertyChanged(nameof(CustomColorsBackToEditorLabel));
        ClearStatusMessage();
        IsCustomColorDeleteConfirmationOpen = false;
        _customColorsPendingDeletion = [];
        CustomColorPendingDeletion = null;
        CurrentPane = CustomColorListPane;
    }

    private void BackFromCustomColorList()
    {
        if (IsCustomColorMultiSelectionActive)
        {
            ExitCustomColorMultiSelection();
            return;
        }

        ClearStatusMessage();
        var returnPane = _customColorListReturnPane;
        _customColorListReturnPane = EditorPane;
        this.RaisePropertyChanged(nameof(CustomColorsBackToEditorLabel));
        SetCurrentPane(returnPane, true);

        // "Manage colors" is a navigation action, not a real color option.
        // Re-notify the relevant binding after returning so the ComboBox restores
        // the actual color selected before the user opened this page.
        if (returnPane == PasswordTagEditorPane)
        {
            this.RaisePropertyChanged(nameof(SelectedPasswordTagColorOption));
        }
        else
        {
            this.RaisePropertyChanged(nameof(SelectedEditorColorOption));
        }
    }

    private void OpenCustomColorPicker()
    {
        SetCustomColorPickerMode(null);
        ResetCustomColorPickerDraft();
        ClearStatusMessage();
        CurrentPane = ColorPane;
    }

    private void OpenCustomColorPickerForEditing(CustomColorItemViewModel customColor)
    {
        SetCustomColorPickerMode(customColor.Id);
        LoadCustomColorPickerDraft(customColor.Name, customColor.ColorCode);
        ClearStatusMessage();
        CurrentPane = ColorPane;
    }

    private void BackFromColorPicker()
    {
        ClearStatusMessage();
        ResetCustomColorPickerDraft();
        SetCustomColorPickerMode(null);
        SetCurrentPane(CustomColorListPane, true);
    }

    private async Task SaveCustomColorAsync()
    {
        if (_isSavingCustomColor)
        {
            return;
        }

        ClearStatusMessage();

        if (!PasswordColorUtility.TryNormalizeHexColor(CustomColorPickerCode, out var colorCode))
        {
            ShowErrorMessage(GetTranslation("Passwords_ColorPicker_InvalidCode"));
            return;
        }

        if (string.Equals(colorCode, PasswordColorUtility.DefaultColor, StringComparison.OrdinalIgnoreCase))
        {
            ShowErrorMessage(GetTranslation("Passwords_CustomColors_Add_DefaultColor"));
            return;
        }

        var colorName = string.IsNullOrWhiteSpace(CustomColorPickerName)
            ? null
            : CustomColorPickerName.Trim();
        var editingCustomColorId = _editingCustomColorId;

        try
        {
            _isSavingCustomColor = true;

            if (editingCustomColorId.HasValue)
            {
                await _endpoints.UpdateCustomUserColorAsync(_token, new UpdateCustomUserColorRequest
                {
                    Id = editingCustomColorId.Value,
                    ColorName = colorName,
                    ClearColorName = colorName is null,
                    ColorCode = colorCode
                });
            }
            else
            {
                await _endpoints.AddCustomUserColorsAsync(_token,
                [
                    new NewCustomUserColorRequest
                    {
                        ColorName = colorName,
                        ColorCode = colorCode
                    }
                ]);
            }

            if (!await RefreshAsync(false))
            {
                return;
            }

            var successMessage = CustomColorSaveSuccessMessage;
            ResetCustomColorPickerDraft();
            SetCustomColorPickerMode(null);
            SetCurrentPane(CustomColorListPane, true);
            ShowSuccessMessage(successMessage);
        }
        catch (Exception ex)
        {
            ShowErrorMessage(GetSafeErrorMessage(ex));
        }
        finally
        {
            _isSavingCustomColor = false;
        }
    }

    private void SetCustomColorPickerMode(Guid? customColorId)
    {
        if (_editingCustomColorId == customColorId)
        {
            return;
        }

        _editingCustomColorId = customColorId;
        this.RaisePropertyChanged(nameof(IsEditingCustomColor));
        this.RaisePropertyChanged(nameof(ColorPickerTitle));
        this.RaisePropertyChanged(nameof(AddColorButtonLabel));
        this.RaisePropertyChanged(nameof(CustomColorSaveSuccessMessage));
    }

    private void ResetCustomColorPickerDraft() =>
        LoadCustomColorPickerDraft(string.Empty, PasswordColorUtility.DefaultColor);

    private void LoadCustomColorPickerDraft(string? name, string colorCode)
    {
        var normalizedColor = PasswordColorUtility.NormalizeKnownColor(colorCode);

        _isUpdatingCustomColorPickerFields = true;
        try
        {
            CustomColorPickerName = name ?? string.Empty;
            _customColorPickerCode = normalizedColor;
            this.RaisePropertyChanged(nameof(CustomColorPickerCode));
            _customColorPickerColor = ColorFromNormalizedArgbHex(normalizedColor);
            this.RaisePropertyChanged(nameof(CustomColorPickerColor));
        }
        finally
        {
            _isUpdatingCustomColorPickerFields = false;
        }
    }

    private void SyncCustomColorPickerCodeFromColor()
    {
        if (_isUpdatingCustomColorPickerFields)
        {
            return;
        }

        _isUpdatingCustomColorPickerFields = true;
        try
        {
            _customColorPickerCode = ToArgbHex(CustomColorPickerColor);
            this.RaisePropertyChanged(nameof(CustomColorPickerCode));
        }
        finally
        {
            _isUpdatingCustomColorPickerFields = false;
        }
    }

    private void SetCustomColorPickerColorFromNormalizedCode(string normalizedColor)
    {
        _isUpdatingCustomColorPickerFields = true;
        try
        {
            _customColorPickerColor = ColorFromNormalizedArgbHex(normalizedColor);
            this.RaisePropertyChanged(nameof(CustomColorPickerColor));
        }
        finally
        {
            _isUpdatingCustomColorPickerFields = false;
        }
    }

    private static Color ColorFromNormalizedArgbHex(string normalizedColor) =>
        Color.FromArgb(
            Convert.ToByte(normalizedColor.Substring(1, 2), 16),
            Convert.ToByte(normalizedColor.Substring(3, 2), 16),
            Convert.ToByte(normalizedColor.Substring(5, 2), 16),
            Convert.ToByte(normalizedColor.Substring(7, 2), 16));

    private static string ToArgbHex(Color color) =>
        $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private Task BeginDeleteCustomColorAsync(CustomColorItemViewModel customColor)
    {
        BeginDeleteCustomColors([customColor]);
        return Task.CompletedTask;
    }

    private void BeginDeleteCustomColors(IReadOnlyList<CustomColorItemViewModel> customColors)
    {
        if (customColors.Count == 0)
        {
            return;
        }

        ClearStatusMessage();
        _customColorsPendingDeletion = customColors.ToArray();
        CustomColorPendingDeletion = _customColorsPendingDeletion[0];
        IsCustomColorDeleteConfirmationOpen = true;
    }

    private void CancelDeleteCustomColor()
    {
        ClearStatusMessage();
        IsCustomColorDeleteConfirmationOpen = false;
        _customColorsPendingDeletion = [];
        CustomColorPendingDeletion = null;
    }

    private async Task ConfirmDeleteCustomColorAsync()
    {
        if (_isDeletingCustomColor || _customColorsPendingDeletion.Count == 0)
        {
            return;
        }

        ClearStatusMessage();
        var customColors = _customColorsPendingDeletion.ToArray();
        var customColorIds = customColors.Select(item => item.Id).ToArray();

        try
        {
            _isDeletingCustomColor = true;
            await _endpoints.DeleteCustomUserColorsAsync(_token, customColorIds);
            CancelDeleteCustomColor();

            if (!await RefreshAsync(false))
            {
                return;
            }

            ShowSuccessMessage(GetTranslation(
                customColors.Length > 1
                    ? "Passwords_CustomColors_Delete_MultipleSuccess"
                    : "Passwords_CustomColors_Delete_Success"));
        }
        catch (Exception ex)
        {
            ShowErrorMessage(GetSafeErrorMessage(ex));
        }
        finally
        {
            _isDeletingCustomColor = false;
        }
    }

    private void ApplyManualColorCode()
    {
        ClearStatusMessage();

        if (!PasswordColorUtility.TryNormalizeHexColor(CustomColorCode, out var normalizedColor))
        {
            ShowErrorMessage(GetTranslation("Passwords_ColorPicker_InvalidCode"));
            return;
        }

        ApplyEditorColor(normalizedColor);
        ClearStatusMessage();
    }

    private void ApplyColorFromSliders()
    {
        if (_isUpdatingColorFields)
        {
            return;
        }

        var normalizedColor = $"#{PasswordColorUtility.ToComponentByte(CustomAlpha):X2}{PasswordColorUtility.ToComponentByte(CustomRed):X2}{PasswordColorUtility.ToComponentByte(CustomGreen):X2}{PasswordColorUtility.ToComponentByte(CustomBlue):X2}";
        ApplyEditorColor(normalizedColor, updateColorFields: false);
    }

    private void ApplyEditorColor(string color, bool updateSelectedPreset = true, bool updateColorFields = true)
    {
        if (!PasswordColorUtility.TryNormalizeHexColor(color, out var normalizedColor))
        {
            normalizedColor = PasswordColorUtility.DefaultColor;
        }

        EditorColor = normalizedColor;
        CustomColorCode = normalizedColor;
        UpdateMoreColorsOption(normalizedColor);

        if (updateColorFields)
        {
            SyncColorFieldsFromEditorColor();
        }

        if (updateSelectedPreset)
        {
            SelectMatchingPresetColor(normalizedColor);
        }
    }

    private void SelectMatchingPresetColor(string normalizedColor)
    {
        var match = PresetColors.FirstOrDefault(item => item.Key != CustomColorKey
            && item.Key != SelectedCustomColorKey
            && string.Equals(PasswordColorUtility.NormalizeKnownColor(item.HexValue), normalizedColor, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            RemoveSelectedCustomColorOption();
            SetSelectedEditorColorOption(match);
            return;
        }

        var customOption = PresetColors.FirstOrDefault(item => item.Key == SelectedCustomColorKey);
        if (customOption is null)
        {
            customOption = new PasswordColorOptionViewModel(SelectedCustomColorKey, normalizedColor, normalizedColor);
            var moreColorsIndex = PresetColors.ToList().FindIndex(item => item.Key == CustomColorKey);
            PresetColors.Insert(moreColorsIndex >= 0 ? moreColorsIndex : PresetColors.Count, customOption);
        }
        else
        {
            customOption.Update(normalizedColor, normalizedColor);
        }

        SetSelectedEditorColorOption(customOption);
    }

    private void UpdateMoreColorsOption(string normalizedColor)
    {
        var moreColorsOption = PresetColors.FirstOrDefault(item => item.Key == CustomColorKey);
        moreColorsOption?.Update(GetTranslation("Passwords_Color_More"), normalizedColor);
    }

    private void RemoveSelectedCustomColorOption()
    {
        var customOption = PresetColors.FirstOrDefault(item => item.Key == SelectedCustomColorKey);
        if (customOption is not null)
        {
            PresetColors.Remove(customOption);
        }
    }

    private void SetSelectedEditorColorOption(PasswordColorOptionViewModel option)
    {
        if (ReferenceEquals(_selectedEditorColorOption, option))
        {
            return;
        }

        _selectedEditorColorOption = option;
        this.RaisePropertyChanged(nameof(SelectedEditorColorOption));
    }

    private void SyncColorFieldsFromEditorColor()
    {
        if (!PasswordColorUtility.TryNormalizeHexColor(EditorColor, out var normalizedColor))
        {
            normalizedColor = PasswordColorUtility.DefaultColor;
        }

        var alpha = Convert.ToByte(normalizedColor.Substring(1, 2), 16);
        var red = Convert.ToByte(normalizedColor.Substring(3, 2), 16);
        var green = Convert.ToByte(normalizedColor.Substring(5, 2), 16);
        var blue = Convert.ToByte(normalizedColor.Substring(7, 2), 16);

        _isUpdatingColorFields = true;
        try
        {
            SetColorField(ref _customAlpha, alpha, nameof(CustomAlpha), nameof(CustomAlphaText));
            SetColorField(ref _customRed, red, nameof(CustomRed), nameof(CustomRedText));
            SetColorField(ref _customGreen, green, nameof(CustomGreen), nameof(CustomGreenText));
            SetColorField(ref _customBlue, blue, nameof(CustomBlue), nameof(CustomBlueText));
            CustomColorCode = normalizedColor;
        }
        finally
        {
            _isUpdatingColorFields = false;
        }
    }

    private void SetColorField(ref double field, double value, string propertyName, string textPropertyName)
    {
        if (Math.Abs(field - value) < 0.01)
        {
            return;
        }

        field = value;
        this.RaisePropertyChanged(propertyName);
        this.RaisePropertyChanged(textPropertyName);
    }



    private enum MultiSelectionExportKind
    {
        None,
        Passwords,
        CustomColors,
        PasswordTags
    }

    protected override void DisposeManaged()
    {
        _searchDebounce.Dispose();
        _customColorSearchDebounce.Dispose();
        Reset();
        base.DisposeManaged();
    }

}
