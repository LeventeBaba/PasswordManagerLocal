using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PasswordManagerLocal.Common.Frontend.Views.Controls;

/// <summary>
/// A ColorView configured for a single visible tab. Avalonia's default ColorView
/// still renders the tab header when only the spectrum tab is enabled, so this
/// control hides that redundant header after the template is applied.
/// </summary>
public sealed class SingleSpectrumColorView : ColorView
{
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        TabControl? tabControl = e.NameScope.Find<TabControl>("PART_TabControl");
        if (tabControl is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () => HideSingleTabHeader(tabControl),
            DispatcherPriority.Background);
    }

    private static void HideSingleTabHeader(TabControl tabControl)
    {
        ItemsPresenter? tabHeaderPresenter = tabControl
            .GetVisualDescendants()
            .OfType<ItemsPresenter>()
            .FirstOrDefault();

        if (tabHeaderPresenter is not null)
        {
            tabHeaderPresenter.IsVisible = false;
        }
    }
}
