using Avalonia.Media;
using PasswordManagerLocal.Common.Contracts.Responses;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

public sealed class CustomColorItemViewModel : MultiSelectableListItemViewModel
{
    private string _deleteLabel;

    private CustomColorItemViewModel(
        CustomUserColorInfoResponse color,
        string deleteLabel,
        Action<CustomColorItemViewModel> edit,
        Func<CustomColorItemViewModel, Task> deleteAsync)
    {
        Id = color.Id;
        Name = color.ColorName;
        ColorCode = PasswordColorUtility.NormalizeKnownColor(color.ColorCode);
        ColorBrush = PasswordColorUtility.ParseBrush(ColorCode);
        _deleteLabel = deleteLabel;

        EditCommand = Own(ReactiveCommand.Create(() => edit(this)));
        DeleteCommand = Own(ReactiveCommand.CreateFromTask(() => deleteAsync(this)));
    }

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

    public ReactiveCommand<RxVoid, RxVoid> EditCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> DeleteCommand { get; }

    public void ApplyDeleteLabel(string deleteLabel) => DeleteLabel = deleteLabel;

    public static CustomColorItemViewModel Create(
        CustomUserColorInfoResponse color,
        string deleteLabel,
        Action<CustomColorItemViewModel> edit,
        Func<CustomColorItemViewModel, Task> deleteAsync) =>
        new(color, deleteLabel, edit, deleteAsync);
}
