using ReactiveUI;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

public abstract class MultiSelectableListItemViewModel : DisposableReactiveObject
{
    private bool _isSelectionModeActive;
    private bool _isSelected;

    public bool IsSelectionModeActive
    {
        get => _isSelectionModeActive;
        private set
        {
            if (_isSelectionModeActive == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _isSelectionModeActive, value);
            this.RaisePropertyChanged(nameof(IsSelectionModeInactive));
        }
    }

    public bool IsSelectionModeInactive => !IsSelectionModeActive;

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    public void SetSelectionModeActive(bool isActive)
    {
        IsSelectionModeActive = isActive;
        if (!isActive)
        {
            IsSelected = false;
        }
    }
}
