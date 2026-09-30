using Avalonia.Media;
using PasswordManagerLocal.Common.Contracts.Responses;
using ReactiveUI;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

public sealed class CustomColorItemViewModel : MultiSelectableListItemViewModel
{
    private string _deleteLabel;

    private CustomColorItemViewModel(
        CustomUserColorInfoResponse color,
        string deleteLabel,
        PasswordsViewModel owner)
    {
        Owner = owner;
        Id = color.Id;
        Name = color.ColorName;
        ColorCode = PasswordColorUtility.NormalizeKnownColor(color.ColorCode);
        ColorBrush = PasswordColorUtility.ParseBrush(ColorCode);
        _deleteLabel = deleteLabel;

    }

    public PasswordsViewModel Owner { get; }

    public override bool IsSelectionModeActive => Owner.IsCustomColorMultiSelectionActive;

    public Guid Id { get; }

    public string? Name { get; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? ColorCode : Name;

    public string ColorCode { get; }

    public IBrush ColorBrush { get; }

    public string DeleteLabel
    {
        get => _deleteLabel;
        private set => this.RaiseAndSetIfChanged(ref _deleteLabel, value);
    }

    public void ApplyDeleteLabel(string deleteLabel) => DeleteLabel = deleteLabel;

    public static CustomColorItemViewModel Create(
        CustomUserColorInfoResponse color,
        string deleteLabel,
        PasswordsViewModel owner) =>
        new(color, deleteLabel, owner);
}
