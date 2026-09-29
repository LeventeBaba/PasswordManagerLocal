namespace PasswordManagerLocal.Common.Backend.Utils;

/// <summary>Starts independent work without inheriting account lock ownership or request context.</summary>
public static class IndependentBackgroundWork
{
    public static Task Run(Func<Task> action, CancellationToken ct = default)
    {
        if (ExecutionContext.IsFlowSuppressed()) return Task.Run(action, ct);
        using (ExecutionContext.SuppressFlow()) return Task.Run(action, ct);
    }
}
