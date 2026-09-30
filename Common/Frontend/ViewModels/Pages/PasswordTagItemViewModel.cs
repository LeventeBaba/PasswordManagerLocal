using Avalonia.Media;
using PasswordManagerLocal.Common.Contracts.Responses;
using ReactiveUI;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

public sealed class PasswordTagItemViewModel : ReactiveObject
{
    private string _removeLabel;

    private PasswordTagItemViewModel(
        PasswordTagInfoResponse tag,
        string removeLabel,
        PasswordsViewModel owner)
    {
        Owner = owner;
        Id = tag.Id;
        Name = tag.Name;
        Color = tag.Color;
        ColorBrush = PasswordColorUtility.ParseBrush(tag.Color);
        _removeLabel = removeLabel;

    }

    public PasswordsViewModel Owner { get; }

    public Guid Id { get; }

    public string Name { get; }

    public string Color { get; }

    public IBrush ColorBrush { get; }

    public string RemoveLabel
    {
        get => _removeLabel;
        private set => this.RaiseAndSetIfChanged(ref _removeLabel, value);
    }

    public void ApplyRemoveLabel(string removeLabel) => RemoveLabel = removeLabel;

    public static PasswordTagItemViewModel Create(
        PasswordTagInfoResponse tag,
        string removeLabel,
        PasswordsViewModel owner) =>
        new(tag, removeLabel, owner);
}
