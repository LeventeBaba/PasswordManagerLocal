using PasswordManagerLocal.Common.Frontend.Theming;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public static class TestAppThemeFactory
{
    public static AppThemeManager CreateManager(
        IAppThemeResourceLoader? loader = null,
        IAppThemeDispatcher? dispatcher = null,
        IAppThemeResourceHost? host = null) =>
        new(
            AppThemeCatalog.CreateDefault(),
            loader ?? new TestAppThemeResourceLoader(),
            dispatcher ?? new TestAppThemeDispatcher(),
            host ?? new TestAppThemeResourceHost());
}
