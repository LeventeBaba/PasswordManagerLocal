namespace PasswordManagerLocal.Common.Frontend.Theming;

public interface IAppThemeDispatcher
{
    Task InvokeAsync(Action operation, CancellationToken cancellationToken = default);
}
