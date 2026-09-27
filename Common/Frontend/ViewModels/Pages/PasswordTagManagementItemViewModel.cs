using Avalonia.Media;
using PasswordManagerLocal.Common.Contracts.Responses;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

public sealed class PasswordTagManagementItemViewModel : MultiSelectableListItemViewModel
{
    private string _deleteLabel;

    private PasswordTagManagementItemViewModel(
        PasswordTagInfoResponse tag,
        string deleteLabel,
        Action<PasswordTagManagementItemViewModel> edit,
        Func<PasswordTagManagementItemViewModel, Task> deleteAsync)
    {
        Id = tag.Id;
        Name = tag.Name;
        Color = PasswordColorUtility.NormalizeKnownColor(tag.Color);
        ColorBrush = PasswordColorUtility.ParseBrush(Color);
        _deleteLabel = deleteLabel;

        EditCommand = Own(ReactiveCommand.Create(() => edit(this)));
        DeleteCommand = Own(ReactiveCommand.CreateFromTask(() => deleteAsync(this)));
    }

    public Guid Id { get; }

    public string Name { get; }

    public string Color { get; }

    public IBrush ColorBrush { get; }

    public string DeleteLabel
    {
        get => _deleteLabel;
        private set => this.RaiseAndSetIfChanged(ref _deleteLabel, value);
    }

    public ReactiveCommand<RxVoid, RxVoid> EditCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> DeleteCommand { get; }

    public void ApplyDeleteLabel(string deleteLabel) => DeleteLabel = deleteLabel;

    public static PasswordTagManagementItemViewModel Create(
        PasswordTagInfoResponse tag,
        string deleteLabel,
        Action<PasswordTagManagementItemViewModel> edit,
        Func<PasswordTagManagementItemViewModel, Task> deleteAsync) =>
        new(tag, deleteLabel, edit, deleteAsync);
}
