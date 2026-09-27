using PasswordManagerLocal.Common.Backend.Abstractions.Services;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeControlledHostedService : ISyncControlledHostedService
{
    private readonly string _name;
    private readonly IList<string> _calls;
    private readonly bool _throwOnStart;

    public FakeControlledHostedService(string name, int startOrder, IList<string> calls, bool throwOnStart = false)
    {
        _name = name;
        StartOrder = startOrder;
        _calls = calls;
        _throwOnStart = throwOnStart;
    }

    public int StartOrder { get; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _calls.Add($"start:{_name}");

        if (_throwOnStart)
            throw new InvalidOperationException($"Start failed for {_name}.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _calls.Add($"stop:{_name}");
        return Task.CompletedTask;
    }
}
