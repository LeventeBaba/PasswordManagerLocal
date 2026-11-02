using PasswordManagerLocal.Common.Contracts.Constants;
using PasswordManagerLocal.Common.Frontend.Services;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

public sealed partial class PasswordsViewModel
{
    private bool _isPasswordTagPickerOpen;
    private bool _isCreatingTagFromPicker;
    private bool _isEditorDiscardConfirmationOpen;
    private string _editorInitialName = string.Empty;
    private string _editorInitialDescription = string.Empty;
    private string _editorInitialColor = PasswordColorUtility.DefaultColor;
    private HashSet<Guid> _editorInitialTagIds = [];
    private bool _hasEditorSecretChanges;
    private bool _isEditorHeaderVisible = true;
    private string? _editorPasswordValidationKey;

    public PasswordTagPickerViewModel TagPicker { get; }
    public ReactiveCommand<RxVoid, RxVoid> OpenPasswordTagPickerCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ConfirmDiscardPasswordEditorCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> KeepEditingPasswordCommand { get; }

    internal double EditorScrollOffset { get; set; }
    internal bool RestoreEditorTagsFocus { get; set; }
    public bool IsPasswordTagPickerVisible => _isPasswordTagPickerOpen && IsEditorPaneVisible;
    public bool IsPasswordPaneContentEnabled => !IsPasswordTagPickerVisible && !IsEditorDiscardConfirmationOpen;
    public bool IsOuterPasswordErrorVisible => IsStatusMessageError && !IsEditorPaneVisible;
    public bool IsEditorInteractive => !_isSavingPassword;
    public bool IsSavingPassword => _isSavingPassword;
    public bool IsEditorHeaderVisible => _isEditorHeaderVisible;
    public bool IsTagManagementFromPicker => _isPasswordTagPickerOpen;
    public string TagManagementContextHint => GetTranslation("Passwords_TagPicker_ManagementHint");

    internal void SetEditorViewportHeight(double height)
    {
        var visible = height >= 260;
        if (_isEditorHeaderVisible == visible)
            return;
        _isEditorHeaderVisible = visible;
        this.RaisePropertyChanged(nameof(IsEditorHeaderVisible));
    }
    public string EditorSaveActionLabel => GetTranslation(_isSavingPassword
        ? "Passwords_Editor_Saving" : IsCreateMode ? "Common_Add" : "Common_Save");
    public string EditorOrganizationLabel => GetTranslation("Passwords_Editor_Organization");
    public string EditorChooseTagsLabel => GetTranslation(HasSelectedEditorTags
        ? "Passwords_Editor_ChangeTags" : "Passwords_Editor_ChooseTags");
    public string EditorTagsSummary => !HasSelectedEditorTags
        ? GetTranslation("Passwords_TagPicker_NoneSelected")
        : string.Join(", ", EditorSelectedTags.Take(2).Select(tag => tag.Name)) +
          (EditorSelectedTags.Count > 2 ? $" · +{EditorSelectedTags.Count - 2}" : string.Empty);
    public int EditorSelectedTagCount => EditorSelectedTags.Count;
    public string EditorTagsAccessibleSummary => string.Format(
        GetTranslation("Passwords_TagPicker_Count"), EditorSelectedTags.Count);
    public string EditorStoredPasswordHint => GetTranslation("Passwords_Editor_StoredPasswordHint");
    public int EditorNameMaxLength => DataLengthConstants.PasswordNameMaxLength;
    public int EditorDescriptionMaxLength => DataLengthConstants.DescriptionMaxLength;
    public bool HasEditorPasswordValidationError => _editorPasswordValidationKey is not null;
    public string EditorPasswordValidationMessage => _editorPasswordValidationKey is { } key
        ? GetTranslation(key) : string.Empty;
    public string EditorDiscardTitle => GetTranslation("Passwords_Editor_Discard_Title");
    public string EditorDiscardMessage => GetTranslation("Passwords_Editor_Discard_Message");
    public string EditorDiscardLabel => GetTranslation("Passwords_Editor_Discard_Confirm");
    public string EditorKeepEditingLabel => GetTranslation("Passwords_Editor_Discard_Keep");

    public bool IsEditorDiscardConfirmationOpen
    {
        get => _isEditorDiscardConfirmationOpen;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isEditorDiscardConfirmationOpen, value);
            this.RaisePropertyChanged(nameof(IsPasswordPaneContentEnabled));
        }
    }

    private void OpenPasswordTagPicker()
    {
        if (!IsEditorPaneVisible || _isSavingPassword || _isPasswordTagPickerOpen)
            return;
        TagPicker.Begin(_allPasswordTags, EditorSelectedTags.Select(tag => tag.Id), EditorColor);
        _isPasswordTagPickerOpen = true;
        PlatformServices.Keyboard.Hide();
        RaiseEditorOverlayStateChanged();
    }

    private void ApplyPasswordTagPicker(IReadOnlyList<Guid> selectedIds, string color)
    {
        if (!_isPasswordTagPickerOpen || !IsEditorPaneVisible)
            return;
        SetEditorSelectedTags(selectedIds);
        ApplyEditorColor(color);
        ClosePasswordTagPicker();
    }

    private void ClosePasswordTagPicker()
    {
        if (!_isPasswordTagPickerOpen)
            return;
        _isPasswordTagPickerOpen = false;
        TagPicker.End();
        PlatformServices.Keyboard.Hide();
        RestoreEditorTagsFocus = true;
        RaiseEditorOverlayStateChanged();
    }

    private void ManageTagsFromPicker()
    {
        if (!IsPasswordTagPickerVisible)
            return;
        _isCreatingTagFromPicker = false;
        PlatformServices.Keyboard.Hide();
        OpenPasswordTagList();
    }

    private void CreateTagFromPicker()
    {
        if (!IsPasswordTagPickerVisible)
            return;
        ManageTagsFromPicker();
        _isCreatingTagFromPicker = true;
        OpenPasswordTagEditor();
    }

    private void RaiseEditorOverlayStateChanged() => RaisePropertiesChanged([
        nameof(IsPasswordTagPickerVisible), nameof(IsPasswordPaneContentEnabled), nameof(IsOuterPasswordErrorVisible),
        nameof(IsTagManagementFromPicker)]);

    private void RaiseEditorPresentationChanged() => RaisePropertiesChanged([
        nameof(EditorSaveActionLabel), nameof(EditorOrganizationLabel), nameof(EditorChooseTagsLabel),
        nameof(EditorTagsSummary), nameof(EditorSelectedTagCount), nameof(EditorTagsAccessibleSummary), nameof(EditorStoredPasswordHint),
        nameof(EditorPasswordValidationMessage), nameof(EditorDiscardTitle), nameof(EditorDiscardMessage),
        nameof(EditorDiscardLabel), nameof(EditorKeepEditingLabel), nameof(TagManagementContextHint)]);

    private void SetEditorSaving(bool value)
    {
        _isSavingPassword = value;
        RaisePropertiesChanged([nameof(IsSavingPassword), nameof(IsEditorInteractive), nameof(EditorSaveActionLabel)]);
    }

    private void CaptureEditorBaseline()
    {
        _editorInitialName = EditorName;
        _editorInitialDescription = EditorDescription;
        _editorInitialColor = EditorColor;
        _editorInitialTagIds = EditorSelectedTags.Select(tag => tag.Id).ToHashSet();
        _hasEditorSecretChanges = false;
        EditorScrollOffset = 0;
        RestoreEditorTagsFocus = false;
    }

    private bool HasEditorDraftChanges =>
        _hasEditorSecretChanges || EditorName != _editorInitialName ||
        EditorDescription != _editorInitialDescription || EditorColor != _editorInitialColor ||
        !_editorInitialTagIds.SetEquals(EditorSelectedTags.Select(tag => tag.Id));

    private void RequestCancelPasswordEditor()
    {
        if (_isSavingPassword)
            return;
        if (HasEditorDraftChanges)
        {
            PlatformServices.Keyboard.Hide();
            IsEditorDiscardConfirmationOpen = true;
            return;
        }
        CancelPasswordEditor();
    }

    protected override void OnStatusMessageChanged() =>
        this.RaisePropertyChanged(nameof(IsOuterPasswordErrorVisible));

    private void ClearEditorPasswordValidation()
    {
        if (_editorPasswordValidationKey is null)
            return;
        _editorPasswordValidationKey = null;
        RaisePropertiesChanged([nameof(HasEditorPasswordValidationError), nameof(EditorPasswordValidationMessage)]);
    }
}
