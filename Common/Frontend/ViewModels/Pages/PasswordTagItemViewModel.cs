using Avalonia.Media;
using PasswordManagerLocal.Common.Contracts.Responses;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

public sealed class PasswordTagItemViewModel : DisposableReactiveObject
{
    private string _removeLabel;

    private PasswordTagItemViewModel(
        PasswordTagInfoResponse tag,
        string removeLabel,
        Action<PasswordTagItemViewModel> select,
        Action<PasswordTagItemViewModel> remove)
    {
        Id = tag.Id;
        Name = tag.Name;
        Color = tag.Color;
        ColorBrush = PasswordColorUtility.ParseBrush(tag.Color);
        _removeLabel = removeLabel;

        SelectCommand = Own(ReactiveCommand.Create(() => select(this)));
        RemoveCommand = Own(ReactiveCommand.Create(() => remove(this)));
    }

    public Guid Id { get; }

    public string Name { get; }

    public string Color { get; }

    public IBrush ColorBrush { get; }

    public string RemoveLabel
    {
        get => _removeLabel;
        private set => this.RaiseAndSetIfChanged(ref _removeLabel, value);
    }

    public ReactiveCommand<RxVoid, RxVoid> SelectCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> RemoveCommand { get; }

    public void ApplyRemoveLabel(string removeLabel) => RemoveLabel = removeLabel;

    public static PasswordTagItemViewModel Create(
        PasswordTagInfoResponse tag,
        string removeLabel,
        Action<PasswordTagItemViewModel> select,
        Action<PasswordTagItemViewModel> remove) =>
        new(tag, removeLabel, select, remove);
}
