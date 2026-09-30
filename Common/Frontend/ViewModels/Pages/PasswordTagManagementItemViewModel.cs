using Avalonia.Media;
using PasswordManagerLocal.Common.Contracts.Responses;
using ReactiveUI;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

public sealed class PasswordTagManagementItemViewModel : MultiSelectableListItemViewModel
{
    private string _deleteLabel;

    private PasswordTagManagementItemViewModel(
        PasswordTagInfoResponse tag,
        string deleteLabel,
        PasswordsViewModel owner)
    {
        Owner = owner;
        Id = tag.Id;
        Name = tag.Name;
        Color = PasswordColorUtility.NormalizeKnownColor(tag.Color);
        ColorBrush = PasswordColorUtility.ParseBrush(Color);
        _deleteLabel = deleteLabel;

    }

    public PasswordsViewModel Owner { get; }

    public override bool IsSelectionModeActive => Owner.IsPasswordTagMultiSelectionActive;

    public Guid Id { get; }

    public string Name { get; }

    public string Color { get; }

    public IBrush ColorBrush { get; }

    public string DeleteLabel
    {
        get => _deleteLabel;
        private set => this.RaiseAndSetIfChanged(ref _deleteLabel, value);
    }

    public void ApplyDeleteLabel(string deleteLabel) => DeleteLabel = deleteLabel;

    public static PasswordTagManagementItemViewModel Create(
        PasswordTagInfoResponse tag,
        string deleteLabel,
        PasswordsViewModel owner) =>
        new(tag, deleteLabel, owner);
}
