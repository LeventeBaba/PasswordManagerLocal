using Avalonia.Controls;
using Avalonia.Input;

namespace PasswordManagerLocal.Common.Frontend.Views.Controls;

public partial class PasswordTagSelectorView : UserControl
{
    public PasswordTagSelectorView() => InitializeComponent();

    public void FocusSelector() => ChooseTagsButton.Focus(NavigationMethod.Directional);
}
