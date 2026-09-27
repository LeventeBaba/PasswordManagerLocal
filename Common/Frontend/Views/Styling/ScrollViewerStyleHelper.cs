using Avalonia.Controls;

namespace PasswordManagerLocal.Common.Frontend.Views.Styling;

internal static class ScrollViewerStyleHelper
{
    private const string ComfortableScrollerClass = "comfortableListScroller";
    private const string AndroidComfortableScrollerClass = "androidComfortableListScroller";

    public static void ApplyComfortableScrollerStyle(ScrollViewer? scrollViewer)
    {
        if (scrollViewer is null)
            return;

        scrollViewer.Classes.Add(ComfortableScrollerClass);

        if (OperatingSystem.IsAndroid())
            scrollViewer.Classes.Add(AndroidComfortableScrollerClass);
    }
}
