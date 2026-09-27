using System.Reflection;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

public class TestEndpointsDispatchProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
            throw new InvalidOperationException("The endpoint proxy invocation has no target method.");
        if (Handler is not null)
            return Handler(targetMethod, args);
        throw new NotSupportedException("The endpoint proxy is only used as an identity token in lifecycle tests.");
    }
}
