using PasswordManagerLocal.Common.Backend.Abstractions.Services;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeBackendHostedService : IBackendHostedService
{
    private readonly string _name;
    private readonly IList<string> _calls;
    private readonly bool _throwOnStart;
    private readonly bool _throwOnStop;

    public FakeBackendHostedService(
        string name,
        IList<string> calls,
        bool throwOnStart = false,
        bool throwOnStop = false)
    {
        _name = name;
        _calls = calls;
        _throwOnStart = throwOnStart;
        _throwOnStop = throwOnStop;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _calls.Add($"start:{_name}");

        if (_throwOnStart)
            throw new InvalidOperationException($"Start failed for {_name}.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _calls.Add($"stop:{_name}");

        if (_throwOnStop)
            throw new InvalidOperationException($"Stop failed for {_name}.");

        return Task.CompletedTask;
    }
}
