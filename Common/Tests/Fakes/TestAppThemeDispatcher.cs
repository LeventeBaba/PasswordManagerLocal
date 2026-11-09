using PasswordManagerLocal.Common.Frontend.Theming;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class TestAppThemeDispatcher : IAppThemeDispatcher
{
    public Func<Action, CancellationToken, Task>? Dispatch { get; set; }

    public int InvocationCount { get; private set; }

    public Task InvokeAsync(
        Action operation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InvocationCount++;
        if (Dispatch is not null)
            return Dispatch(operation, cancellationToken);
        operation();
        return Task.CompletedTask;
    }
}
