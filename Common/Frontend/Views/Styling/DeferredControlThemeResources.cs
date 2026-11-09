using Avalonia;
using Avalonia.Threading;

namespace PasswordManagerLocal.Common.Frontend.Views.Styling;

/// <summary>Public control templates only; no Activity, session, view model or account data is cached.</summary>
internal static class DeferredControlThemeResources
{
    public static void EnsureLoaded()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (Application.Current is App app)
            app.EnsureFeatureControlThemes();
    }
}
