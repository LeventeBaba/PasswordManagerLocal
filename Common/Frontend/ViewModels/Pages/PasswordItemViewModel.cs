using Avalonia.Media;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Frontend.Services;
using ReactiveUI;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

public sealed class PasswordItemViewModel : MultiSelectableListItemViewModel
{
    private string _editLabel;
    private string _deleteLabel;
    private string _colorName;

    private PasswordItemViewModel(
        PasswordInfoResponse password,
        IReadOnlyList<string> tagNames,
        string? colorName,
        string editLabel,
        string deleteLabel,
        PasswordsViewModel owner)
    {
        Owner = owner;
        Id = password.Id;
        Name = password.Name;
        Description = password.Description;
        TagIds = password.TagIds.ToList();
        TagNames = tagNames;
        Color = password.Color;
        _colorName = colorName?.Trim() ?? string.Empty;
        CreatedAt = password.CreatedAt;
        LastUpdatedAt = password.LastUpdatedAt;
        ColorBrush = ParseBrush(password.Color);
        _editLabel = editLabel;
        _deleteLabel = deleteLabel;

    }

    public PasswordsViewModel Owner { get; }

    public override bool IsSelectionModeActive => Owner.IsPasswordMultiSelectionActive;

    public Guid Id { get; }

    public string Name { get; }

    public string Description { get; }

    public IReadOnlyList<Guid> TagIds { get; }

    public IReadOnlyList<string> TagNames { get; }

    public string Color { get; }

    public string ColorName
    {
        get => _colorName;
        private set => this.RaiseAndSetIfChanged(ref _colorName, value);
    }

    public bool HasColorName => !string.IsNullOrWhiteSpace(ColorName);

    public bool HasNoColorName => !HasColorName;

    public DateTime CreatedAt { get; }

    public DateTime LastUpdatedAt { get; }

    public IBrush ColorBrush { get; }

    public string EditLabel
    {
        get => _editLabel;
        private set => this.RaiseAndSetIfChanged(ref _editLabel, value);
    }

    public string DeleteLabel
    {
        get => _deleteLabel;
        private set => this.RaiseAndSetIfChanged(ref _deleteLabel, value);
    }

    public string DescriptionPreview => string.IsNullOrWhiteSpace(Description) ? "—" : Description;

    public string ListDescriptionPreview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Description))
            {
                return "—";
            }

            var normalizedDescription = Description.Replace("\r\n", "\n").Replace('\r', '\n');
            var firstLineBreakIndex = normalizedDescription.IndexOf('\n');
            var firstLine = firstLineBreakIndex >= 0
                ? normalizedDescription[..firstLineBreakIndex]
                : normalizedDescription;

            firstLine = firstLine.Trim();

            return string.IsNullOrWhiteSpace(firstLine) ? "—" : firstLine;
        }
    }

    public string CreatedAtText => FrontendDateTimeUtil.ToLocalFromBackendUtc(CreatedAt).ToString("g");

    public string LastUpdatedAtText => FrontendDateTimeUtil.ToLocalFromBackendUtc(LastUpdatedAt).ToString("g");

    public bool HasTagMatching(string searchTerm) =>
        TagNames.Any(tagName => tagName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase));

    public void ApplyActionLabels(string editLabel, string deleteLabel)
    {
        EditLabel = editLabel;
        DeleteLabel = deleteLabel;
    }

    public void ApplyColorName(string? colorName)
    {
        var normalizedColorName = colorName?.Trim() ?? string.Empty;
        if (string.Equals(ColorName, normalizedColorName, StringComparison.Ordinal))
        {
            return;
        }

        ColorName = normalizedColorName;
        this.RaisePropertyChanged(nameof(HasColorName));
        this.RaisePropertyChanged(nameof(HasNoColorName));
    }

    public static PasswordItemViewModel Create(
        PasswordInfoResponse password,
        IReadOnlyList<string> tagNames,
        string? colorName,
        string editLabel,
        string deleteLabel,
        PasswordsViewModel owner) =>
        new(password, tagNames, colorName, editLabel, deleteLabel, owner);

    private static IBrush ParseBrush(string color) =>
        PasswordColorUtility.ParseBrush(color);
}
