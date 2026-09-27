using Avalonia.Media;
using PasswordManagerLocal.Common.Contracts.Constants;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;
using ReactiveUI;
using System.Collections.ObjectModel;
using System.ComponentModel;
using ReactiveUI.Primitives;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

public sealed partial class PasswordsViewModel
{
    private const string PasswordTagListPane = "password-tag-list";
    private const string PasswordTagEditorPane = "password-tag-editor";
    private const string PasswordTagSelectedCustomColorKey = "password-tag-selected-custom";

    private readonly List<PasswordTagManagementItemViewModel> _allManagedPasswordTags = [];
    private PasswordTagManagementItemViewModel? _passwordTagPendingDeletion;
    private IReadOnlyList<PasswordTagManagementItemViewModel> _passwordTagsPendingDeletion = [];
    private bool _isPasswordTagDeleteConfirmationOpen;
    private bool _isDeletingPasswordTag;
    private bool _isSavingPasswordTag;
    private Guid? _editingPasswordTagId;
    private string _passwordTagEditorName = string.Empty;
    private string _passwordTagEditorColor = PasswordColorUtility.DefaultColor;
    private PasswordColorOptionViewModel? _selectedPasswordTagColorOption;
    private string _passwordTagSearchQuery = string.Empty;
    private bool _isPasswordTagSearchNameEnabled = true;
    private bool _isPasswordTagSearchColorEnabled = true;
    private string _passwordTagSortKey = "name-asc";
    private bool _isPasswordTagMultiSelectionActive;
    private string _customColorListReturnPane = EditorPane;

    public ObservableCollection<PasswordTagManagementItemViewModel> PasswordTags { get; } = [];

    public ObservableCollection<PasswordColorOptionViewModel> PasswordTagColorOptions { get; } = [];

    public ReactiveCommand<RxVoid, RxVoid> OpenPasswordTagListCommand { get; private set; } = null!;

    public ReactiveCommand<RxVoid, RxVoid> BackFromPasswordTagListCommand { get; private set; } = null!;

    public ReactiveCommand<RxVoid, RxVoid> OpenPasswordTagEditorCommand { get; private set; } = null!;

    public ReactiveCommand<RxVoid, RxVoid> BackFromPasswordTagEditorCommand { get; private set; } = null!;

    public ReactiveCommand<RxVoid, RxVoid> SavePasswordTagCommand { get; private set; } = null!;

    public ReactiveCommand<RxVoid, RxVoid> SearchPasswordTagsCommand { get; private set; } = null!;

    public ReactiveCommand<string, RxVoid> SelectPasswordTagSortOptionCommand { get; private set; } = null!;

    public ReactiveCommand<RxVoid, RxVoid> ConfirmDeletePasswordTagCommand { get; private set; } = null!;

    public ReactiveCommand<RxVoid, RxVoid> CancelDeletePasswordTagCommand { get; private set; } = null!;

    public bool IsPasswordTagListPaneVisible => CurrentPane == PasswordTagListPane;

    public bool IsPasswordTagEditorPaneVisible => CurrentPane == PasswordTagEditorPane;

    public bool IsEditingPasswordTag => _editingPasswordTagId.HasValue;

    public string PasswordTagEditorName
    {
        get => _passwordTagEditorName;
        set => this.RaiseAndSetIfChanged(ref _passwordTagEditorName, value ?? string.Empty);
    }

    public int PasswordTagNameMaxLength => DataLengthConstants.PasswordTagNameMaxLength;

    public string PasswordTagEditorColor
    {
        get => _passwordTagEditorColor;
        private set
        {
            this.RaiseAndSetIfChanged(ref _passwordTagEditorColor, value);
            this.RaisePropertyChanged(nameof(PasswordTagEditorColorBrush));
        }
    }

    public IBrush PasswordTagEditorColorBrush => PasswordColorUtility.ParseBrush(PasswordTagEditorColor);

    public PasswordColorOptionViewModel? SelectedPasswordTagColorOption
    {
        get => _selectedPasswordTagColorOption;
        set
        {
            if (value?.IsManageColorsOption == true)
            {
                OpenCustomColorList(PasswordTagEditorPane);
                return;
            }

            if (ReferenceEquals(_selectedPasswordTagColorOption, value))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedPasswordTagColorOption, value);
            if (value is not null)
            {
                ApplyPasswordTagEditorColor(value.HexValue);
            }
        }
    }

    public string PasswordTagSearchQuery
    {
        get => _passwordTagSearchQuery;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_passwordTagSearchQuery, value, StringComparison.Ordinal))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _passwordTagSearchQuery, value);
            ApplyPasswordTagFiltersAndSorting();
        }
    }

    public bool IsPasswordTagSearchNameEnabled
    {
        get => _isPasswordTagSearchNameEnabled;
        set => SetPasswordTagSearchMode(
            ref _isPasswordTagSearchNameEnabled,
            value,
            nameof(IsPasswordTagSearchNameEnabled));
    }

    public bool IsPasswordTagSearchColorEnabled
    {
        get => _isPasswordTagSearchColorEnabled;
        set => SetPasswordTagSearchMode(
            ref _isPasswordTagSearchColorEnabled,
            value,
            nameof(IsPasswordTagSearchColorEnabled));
    }

    public bool CanTogglePasswordTagSearchName =>
        CanTogglePasswordTagSearchMode(IsPasswordTagSearchNameEnabled);

    public bool CanTogglePasswordTagSearchColor =>
        CanTogglePasswordTagSearchMode(IsPasswordTagSearchColorEnabled);

    public bool HasPasswordTags => PasswordTags.Count > 0;

    public bool HasStoredPasswordTags => _allManagedPasswordTags.Count > 0;

    public bool IsPasswordTagListEmpty => !HasStoredPasswordTags;

    public bool IsPasswordTagSearchResultEmpty => HasStoredPasswordTags && !HasPasswordTags;

    public bool IsPasswordTagMultiSelectionActive
    {
        get => _isPasswordTagMultiSelectionActive;
        private set
        {
            if (_isPasswordTagMultiSelectionActive == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _isPasswordTagMultiSelectionActive, value);
            RaiseMultiSelectionStateChanged();
        }
    }

    public bool IsPasswordTagDeleteConfirmationOpen
    {
        get => _isPasswordTagDeleteConfirmationOpen;
        private set => this.RaiseAndSetIfChanged(ref _isPasswordTagDeleteConfirmationOpen, value);
    }

    public PasswordTagManagementItemViewModel? PasswordTagPendingDeletion
    {
        get => _passwordTagPendingDeletion;
        private set
        {
            this.RaiseAndSetIfChanged(ref _passwordTagPendingDeletion, value);
            this.RaisePropertyChanged(nameof(PasswordTagDeleteConfirmationMessage));
        }
    }

    public string PasswordTagsTitle => GetTranslation("Passwords_Tags_Title");

    public string PasswordTagsBackToEditorLabel => GetTranslation("Passwords_Tags_BackToEditor");

    public string PasswordTagsEmptyTitle => GetTranslation("Passwords_Tags_Empty_Title");

    public string PasswordTagsEmptyDescription => GetTranslation("Passwords_Tags_Empty_Description");

    public string PasswordTagsEmptyAddLabel => GetTranslation("Passwords_Tags_Empty_Add");

    public string PasswordTagsSearchEmptyTitle => GetTranslation("Passwords_Tags_SearchEmpty_Title");

    public string PasswordTagsSearchEmptyDescription => GetTranslation("Passwords_Tags_SearchEmpty_Description");

    public string PasswordTagsSearchPlaceholder => GetTranslation("Passwords_Tags_Search_Placeholder");

    public string PasswordTagsSearchModeLabel => GetTranslation("Passwords_Tags_SearchMode_Label");

    public string PasswordTagsSearchModeNameLabel => GetTranslation("Common_Name");

    public string PasswordTagsSearchModeColorLabel => GetTranslation("Passwords_Tags_SearchMode_ColorCode");

    public string PasswordTagsSortLabel => GetTranslation("Passwords_Tags_Sort_Label");

    public string PasswordTagsSortNameAscMenuLabel =>
        BuildPasswordTagSortMenuLabel("name-asc", "Passwords_Sort_NameAsc");

    public string PasswordTagsSortNameDescMenuLabel =>
        BuildPasswordTagSortMenuLabel("name-desc", "Passwords_Sort_NameDesc");

    public string AddPasswordTagLabel => GetTranslation("Passwords_Tags_Add");

    public string PasswordTagEditorTitle => GetTranslation(
        IsEditingPasswordTag ? "Passwords_Tags_Editor_EditTitle" : "Passwords_Tags_Editor_CreateTitle");

    public string PasswordTagEditorNameLabel => GetTranslation("Common_Name");

    public string PasswordTagEditorNamePlaceholder => GetTranslation("Passwords_Tags_Editor_NamePlaceholder");

    public string PasswordTagEditorColorLabel => GetTranslation("Common_Color");

    public string PasswordTagEditorSaveLabel => GetTranslation(
        IsEditingPasswordTag ? "Common_Save" : "Common_Add");

    public string PasswordTagEditorBackLabel => GetTranslation("Passwords_Tags_Editor_Back");

    public string PasswordTagDeleteConfirmationTitle => GetTranslation(
        _passwordTagsPendingDeletion.Count > 1
            ? "Passwords_Tags_DeleteConfirm_MultipleTitle"
            : "Passwords_Tags_DeleteConfirm_Title");

    public string PasswordTagDeleteConfirmationMessage => _passwordTagsPendingDeletion.Count > 1
        ? string.Format(
            GetTranslation("Passwords_Tags_DeleteConfirm_MultipleMessage"),
            _passwordTagsPendingDeletion.Count)
        : string.Format(
            GetTranslation("Passwords_Tags_DeleteConfirm_Message"),
            PasswordTagPendingDeletion?.Name ?? string.Empty);

    public string ConfirmDeletePasswordTagLabel => GetTranslation("Passwords_Tags_DeleteConfirm_Confirm");

    private void InitializePasswordTagManagement()
    {
        OpenPasswordTagListCommand = Own(ReactiveCommand.Create(OpenPasswordTagList));
        BackFromPasswordTagListCommand = Own(ReactiveCommand.Create(BackFromPasswordTagList));
        OpenPasswordTagEditorCommand = Own(ReactiveCommand.Create(OpenPasswordTagEditor));
        BackFromPasswordTagEditorCommand = Own(ReactiveCommand.Create(BackFromPasswordTagEditor));
        SavePasswordTagCommand = Own(ReactiveCommand.CreateFromTask(SavePasswordTagAsync));
        SearchPasswordTagsCommand = Own(ReactiveCommand.Create(ApplyPasswordTagFiltersAndSorting));
        SelectPasswordTagSortOptionCommand = Own(ReactiveCommand.Create<string>(SelectPasswordTagSortOption));
        ConfirmDeletePasswordTagCommand = Own(ReactiveCommand.CreateFromTask(ConfirmDeletePasswordTagAsync));
        CancelDeletePasswordTagCommand = Own(ReactiveCommand.Create(CancelDeletePasswordTag));

        RebuildPasswordTagColorOptions();
        ApplyPasswordTagEditorColor(PasswordColorUtility.DefaultColor);
    }

    private void OpenPasswordTagList()
    {
        ClearStatusMessage();
        ResetPasswordTagDeleteState();
        CurrentPane = PasswordTagListPane;
    }

    private void BackFromPasswordTagList()
    {
        if (IsPasswordTagMultiSelectionActive)
        {
            ExitPasswordTagMultiSelection();
            return;
        }

        ClearStatusMessage();
        SetCurrentPane(EditorPane, true);
        RefreshEditorTagSuggestions();
    }

    private void OpenPasswordTagEditor()
    {
        SetPasswordTagEditorMode(null);
        PasswordTagEditorName = string.Empty;
        ApplyPasswordTagEditorColor(PasswordColorUtility.DefaultColor);
        ClearStatusMessage();
        CurrentPane = PasswordTagEditorPane;
    }

    private void OpenPasswordTagEditorForEditing(PasswordTagManagementItemViewModel tag)
    {
        SetPasswordTagEditorMode(tag.Id);
        PasswordTagEditorName = tag.Name;
        ApplyPasswordTagEditorColor(tag.Color);
        ClearStatusMessage();
        CurrentPane = PasswordTagEditorPane;
    }

    private void BackFromPasswordTagEditor()
    {
        ClearStatusMessage();
        ResetPasswordTagEditorDraft();
        SetCurrentPane(PasswordTagListPane, true);
    }

    private async Task SavePasswordTagAsync()
    {
        if (_isSavingPasswordTag)
        {
            return;
        }

        ClearStatusMessage();
        var name = PasswordTagEditorName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowErrorMessage(GetTranslation("Error_InvalidInput"));
            return;
        }

        if (!PasswordColorUtility.TryNormalizeHexColor(PasswordTagEditorColor, out var color))
        {
            ShowErrorMessage(GetTranslation("Passwords_ColorPicker_InvalidCode"));
            return;
        }

        var editingId = _editingPasswordTagId;

        try
        {
            _isSavingPasswordTag = true;
            if (editingId.HasValue)
            {
                await _endpoints.UpdatePasswordTagAsync(_token, new UpdatePasswordTagRequest
                {
                    Id = editingId.Value,
                    Name = name,
                    Color = color
                });
            }
            else
            {
                await _endpoints.AddPasswordTagAsync(_token, new NewPasswordTagRequest
                {
                    Name = name,
                    Color = color
                });
            }

            if (!await RefreshAsync(false))
            {
                return;
            }

            var successKey = editingId.HasValue
                ? "Passwords_Tags_Update_Success"
                : "Passwords_Tags_Add_Success";
            ResetPasswordTagEditorDraft();
            SetCurrentPane(PasswordTagListPane, true);
            ShowSuccessMessage(GetTranslation(successKey));
        }
        catch (Exception ex)
        {
            ShowErrorMessage(GetSafeErrorMessage(ex));
        }
        finally
        {
            _isSavingPasswordTag = false;
        }
    }

    private void SetPasswordTagEditorMode(Guid? id)
    {
        _editingPasswordTagId = id;
        this.RaisePropertyChanged(nameof(IsEditingPasswordTag));
        this.RaisePropertyChanged(nameof(PasswordTagEditorTitle));
        this.RaisePropertyChanged(nameof(PasswordTagEditorSaveLabel));
    }

    private void ResetPasswordTagEditorDraft()
    {
        SetPasswordTagEditorMode(null);
        PasswordTagEditorName = string.Empty;
        ApplyPasswordTagEditorColor(PasswordColorUtility.DefaultColor);
    }

    private void RebuildManagedPasswordTagItems(IEnumerable<PasswordTagInfoResponse> tags)
    {
        ClearManagedPasswordTagItems();

        foreach (var tag in tags)
        {
            var item = PasswordTagManagementItemViewModel.Create(
                tag,
                DeletePasswordLabel,
                OpenPasswordTagEditorForEditing,
                BeginDeletePasswordTagAsync);
            item.PropertyChanged += HandleManagedPasswordTagItemPropertyChanged;
            _allManagedPasswordTags.Add(item);
        }

        ApplyPasswordTagFiltersAndSorting();
    }

    private void ClearManagedPasswordTagItems()
    {
        foreach (var tag in _allManagedPasswordTags)
        {
            tag.PropertyChanged -= HandleManagedPasswordTagItemPropertyChanged;
            tag.Dispose();
        }

        _allManagedPasswordTags.Clear();
        PasswordTags.Clear();
        RaisePasswordTagCollectionStateChanged();
    }

    private void ApplyPasswordTagFiltersAndSorting()
    {
        IEnumerable<PasswordTagManagementItemViewModel> query = _allManagedPasswordTags;

        if (!string.IsNullOrWhiteSpace(PasswordTagSearchQuery))
        {
            var searchTerm = PasswordTagSearchQuery.Trim();
            query = query.Where(tag =>
                (IsPasswordTagSearchNameEnabled
                 && tag.Name.Contains(searchTerm, StringComparison.CurrentCultureIgnoreCase))
                || (IsPasswordTagSearchColorEnabled
                    && tag.Color.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)));
        }

        query = _passwordTagSortKey == "name-desc"
            ? query.OrderByDescending(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(tag => tag.Id)
            : query.OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(tag => tag.Id);

        PasswordTags.Clear();
        foreach (var tag in query)
        {
            PasswordTags.Add(tag);
        }

        RaisePasswordTagCollectionStateChanged();
    }

    private void RaisePasswordTagCollectionStateChanged()
    {
        this.RaisePropertyChanged(nameof(HasPasswordTags));
        this.RaisePropertyChanged(nameof(HasStoredPasswordTags));
        this.RaisePropertyChanged(nameof(IsPasswordTagListEmpty));
        this.RaisePropertyChanged(nameof(IsPasswordTagSearchResultEmpty));
        RaiseMultiSelectionStateChanged();
    }

    private void SetPasswordTagSearchMode(ref bool field, bool value, string propertyName)
    {
        if (field == value)
        {
            return;
        }

        if (!value && EnabledPasswordTagSearchModeCount <= 1)
        {
            RaisePasswordTagSearchModeToggleProperties();
            return;
        }

        field = value;
        this.RaisePropertyChanged(propertyName);
        RaisePasswordTagSearchModeToggleProperties();
        ApplyPasswordTagFiltersAndSorting();
    }

    private bool CanTogglePasswordTagSearchMode(bool isEnabled) =>
        !isEnabled || EnabledPasswordTagSearchModeCount > 1;

    private int EnabledPasswordTagSearchModeCount =>
        (_isPasswordTagSearchNameEnabled ? 1 : 0)
        + (_isPasswordTagSearchColorEnabled ? 1 : 0);

    private void RaisePasswordTagSearchModeToggleProperties()
    {
        this.RaisePropertyChanged(nameof(CanTogglePasswordTagSearchName));
        this.RaisePropertyChanged(nameof(CanTogglePasswordTagSearchColor));
    }

    private void SelectPasswordTagSortOption(string key)
    {
        if (key is not ("name-asc" or "name-desc"))
        {
            return;
        }

        _passwordTagSortKey = key;
        RaisePasswordTagSortMenuLabelProperties();
        ApplyPasswordTagFiltersAndSorting();
    }

    private string BuildPasswordTagSortMenuLabel(string key, string translationKey) =>
        $"{(_passwordTagSortKey == key ? "✓ " : string.Empty)}{GetTranslation(translationKey)}";

    private void RaisePasswordTagSortMenuLabelProperties()
    {
        this.RaisePropertyChanged(nameof(PasswordTagsSortNameAscMenuLabel));
        this.RaisePropertyChanged(nameof(PasswordTagsSortNameDescMenuLabel));
    }

    private Task BeginDeletePasswordTagAsync(PasswordTagManagementItemViewModel tag)
    {
        BeginDeletePasswordTags([tag]);
        return Task.CompletedTask;
    }

    private void BeginDeletePasswordTags(IReadOnlyList<PasswordTagManagementItemViewModel> tags)
    {
        if (tags.Count == 0)
        {
            return;
        }

        ClearStatusMessage();
        _passwordTagsPendingDeletion = tags.ToArray();
        PasswordTagPendingDeletion = _passwordTagsPendingDeletion[0];
        this.RaisePropertyChanged(nameof(PasswordTagDeleteConfirmationTitle));
        this.RaisePropertyChanged(nameof(PasswordTagDeleteConfirmationMessage));
        IsPasswordTagDeleteConfirmationOpen = true;
    }

    private void CancelDeletePasswordTag()
    {
        ClearStatusMessage();
        ResetPasswordTagDeleteState();
    }

    private void ResetPasswordTagDeleteState()
    {
        IsPasswordTagDeleteConfirmationOpen = false;
        _passwordTagsPendingDeletion = [];
        PasswordTagPendingDeletion = null;
        this.RaisePropertyChanged(nameof(PasswordTagDeleteConfirmationTitle));
        this.RaisePropertyChanged(nameof(PasswordTagDeleteConfirmationMessage));
    }

    private async Task ConfirmDeletePasswordTagAsync()
    {
        if (_isDeletingPasswordTag || _passwordTagsPendingDeletion.Count == 0)
        {
            return;
        }

        var tags = _passwordTagsPendingDeletion.ToArray();

        try
        {
            _isDeletingPasswordTag = true;
            ClearStatusMessage();

            foreach (var tag in tags)
            {
                await _endpoints.DeletePasswordTagAsync(_token, tag.Id);
            }

            ResetPasswordTagDeleteState();
            if (!await RefreshAsync(false))
            {
                return;
            }

            ShowSuccessMessage(GetTranslation(
                tags.Length > 1
                    ? "Passwords_Tags_Delete_MultipleSuccess"
                    : "Passwords_Tags_Delete_Success"));
        }
        catch (Exception ex)
        {
            ShowErrorMessage(GetSafeErrorMessage(ex));
        }
        finally
        {
            _isDeletingPasswordTag = false;
        }
    }

    public void BeginPasswordTagMultiSelection(PasswordTagManagementItemViewModel tag)
    {
        if (!IsPasswordTagListPaneVisible || !_allManagedPasswordTags.Contains(tag))
        {
            return;
        }

        if (!IsPasswordTagMultiSelectionActive)
        {
            IsPasswordTagMultiSelectionActive = true;
            SetSelectionMode(_allManagedPasswordTags, true);
        }

        tag.IsSelected = true;
    }

    private void ExitPasswordTagMultiSelection()
    {
        IsPasswordTagMultiSelectionActive = false;
        SetSelectionMode(_allManagedPasswordTags, false);
    }

    private void HandleManagedPasswordTagItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MultiSelectableListItemViewModel.IsSelected))
        {
            RaiseMultiSelectionStateChanged();
        }
    }

    private void RebuildPasswordTagColorOptions()
    {
        var currentColor = PasswordTagEditorColor;
        PasswordTagColorOptions.Clear();
        PasswordTagColorOptions.Add(new PasswordColorOptionViewModel(
            "tag-teal",
            GetTranslation("Passwords_Color_Teal"),
            PasswordColorUtility.DefaultColor));

        foreach (var customColor in _savedCustomColors)
        {
            var displayName = string.IsNullOrWhiteSpace(customColor.ColorName)
                ? customColor.ColorCode
                : customColor.ColorName;
            PasswordTagColorOptions.Add(new PasswordColorOptionViewModel(
                $"tag-saved-custom:{customColor.Id:N}",
                displayName,
                customColor.ColorCode));
        }

        PasswordTagColorOptions.Add(new PasswordColorOptionViewModel(
            "tag-manage-colors",
            GetTranslation("Passwords_Color_More"),
            currentColor,
            isManageColorsOption: true));

        ApplyPasswordTagEditorColor(currentColor);
    }

    private void ApplyPasswordTagEditorColor(string color)
    {
        if (!PasswordColorUtility.TryNormalizeHexColor(color, out var normalizedColor))
        {
            normalizedColor = PasswordColorUtility.DefaultColor;
        }

        PasswordTagEditorColor = normalizedColor;
        var manageOption = PasswordTagColorOptions.FirstOrDefault(item => item.IsManageColorsOption);
        manageOption?.Update(GetTranslation("Passwords_Color_More"), normalizedColor);

        var match = PasswordTagColorOptions.FirstOrDefault(item =>
            !item.IsManageColorsOption
            && item.Key != PasswordTagSelectedCustomColorKey
            && string.Equals(
                PasswordColorUtility.NormalizeKnownColor(item.HexValue),
                normalizedColor,
                StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            match = PasswordTagColorOptions.FirstOrDefault(item => item.Key == PasswordTagSelectedCustomColorKey);
            if (match is null)
            {
                match = new PasswordColorOptionViewModel(
                    PasswordTagSelectedCustomColorKey,
                    normalizedColor,
                    normalizedColor);
                var manageIndex = PasswordTagColorOptions.ToList().FindIndex(item => item.IsManageColorsOption);
                PasswordTagColorOptions.Insert(
                    manageIndex >= 0 ? manageIndex : PasswordTagColorOptions.Count,
                    match);
            }
            else
            {
                match.Update(normalizedColor, normalizedColor);
            }
        }
        else
        {
            var custom = PasswordTagColorOptions.FirstOrDefault(item => item.Key == PasswordTagSelectedCustomColorKey);
            if (custom is not null)
            {
                PasswordTagColorOptions.Remove(custom);
            }
        }

        SetSelectedPasswordTagColorOption(match);
    }

    private void SetSelectedPasswordTagColorOption(PasswordColorOptionViewModel option)
    {
        if (ReferenceEquals(_selectedPasswordTagColorOption, option))
        {
            return;
        }

        _selectedPasswordTagColorOption = option;
        this.RaisePropertyChanged(nameof(SelectedPasswordTagColorOption));
    }

    private void ApplyPasswordTagLocalization()
    {
        foreach (var tag in _allManagedPasswordTags)
        {
            tag.ApplyDeleteLabel(DeletePasswordLabel);
        }

        var currentColor = PasswordTagEditorColor;
        RebuildPasswordTagColorOptions();
        ApplyPasswordTagEditorColor(currentColor);
        RaisePasswordTagSearchModeToggleProperties();
        RaisePasswordTagSortMenuLabelProperties();
        ApplyPasswordTagFiltersAndSorting();
    }

    private void ResetPasswordTagManagementState()
    {
        ExitPasswordTagMultiSelection();
        ResetPasswordTagDeleteState();
        ClearManagedPasswordTagItems();
        PasswordTagSearchQuery = string.Empty;
        _passwordTagSortKey = "name-asc";
        RaisePasswordTagSortMenuLabelProperties();
        ResetPasswordTagEditorDraft();
    }
}
