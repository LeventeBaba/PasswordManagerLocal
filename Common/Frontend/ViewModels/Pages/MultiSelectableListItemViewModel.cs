using ReactiveUI;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

public abstract class MultiSelectableListItemViewModel : ReactiveObject
{
    private bool _isSelected;

    // Selection mode belongs to the owner, so changing it never walks every row.
    public abstract bool IsSelectionModeActive { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}
