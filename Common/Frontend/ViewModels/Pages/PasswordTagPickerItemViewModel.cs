using ReactiveUI;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

/// <summary>A checkbox in an assignment draft, independent of tag-management selection.</summary>
public sealed class PasswordTagPickerItemViewModel : ReactiveObject
{
    private readonly Action<PasswordTagPickerItemViewModel> _selectionChanged;
    private bool _isSelected;

    internal PasswordTagPickerItemViewModel(
        PasswordTagItemViewModel tag,
        bool isSelected,
        Action<PasswordTagPickerItemViewModel> selectionChanged)
    {
        Id = tag.Id;
        Name = tag.Name;
        Color = tag.Color;
        _isSelected = isSelected;
        _selectionChanged = selectionChanged;
    }

    public Guid Id { get; }
    public string Name { get; }
    public string Color { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            this.RaiseAndSetIfChanged(ref _isSelected, value);
            _selectionChanged(this);
        }
    }
}
