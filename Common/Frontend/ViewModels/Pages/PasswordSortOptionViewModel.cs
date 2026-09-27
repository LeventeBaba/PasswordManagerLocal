using ReactiveUI;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

public sealed class PasswordSortOptionViewModel : ReactiveObject
{
    private bool _isSelected;

    public PasswordSortOptionViewModel(string key, string displayName)
    {
        Key = key;
        DisplayName = displayName;
    }

    public string Key { get; }

    public string DisplayName { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;

            this.RaiseAndSetIfChanged(ref _isSelected, value);
            this.RaisePropertyChanged(nameof(SelectionMark));
        }
    }

    public string SelectionMark => IsSelected ? "✓" : string.Empty;
}
