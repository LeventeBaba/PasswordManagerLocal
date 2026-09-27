namespace PasswordManagerLocal.Windows.EndpointRpc.Server;

internal sealed class EndpointInvocationTracker
{
    private int _stage = (int)EndpointInvocationStage.BeforeInvocation;

    public EndpointInvocationStage Stage => (EndpointInvocationStage)Volatile.Read(ref _stage);

    public void MarkInvoking() => AdvanceTo(EndpointInvocationStage.Invoking);
    public void MarkInvocationCompleted() => AdvanceTo(EndpointInvocationStage.InvocationCompleted);
    public void MarkResponseValidated() => AdvanceTo(EndpointInvocationStage.ResponseValidated);
    public void MarkResponseSerialized() => AdvanceTo(EndpointInvocationStage.ResponseSerialized);

    private void AdvanceTo(EndpointInvocationStage stage)
    {
        while (true)
        {
            var current = Volatile.Read(ref _stage);
            if (current >= (int)stage)
            {
                if (current == (int)stage)
                    return;
                throw new InvalidOperationException("The endpoint invocation stage cannot move backwards.");
            }

            if (Interlocked.CompareExchange(ref _stage, (int)stage, current) == current)
                return;
        }
    }
}
