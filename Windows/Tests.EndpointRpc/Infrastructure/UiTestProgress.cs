using System.Collections.Concurrent;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

internal sealed class UiTestProgress(Action<string>? writeLine = null)
{
    private readonly ConcurrentQueue<string> _entries = new();

    public void Record(string message)
    {
        var entry = $"[{DateTimeOffset.UtcNow:O}] {message}";
        _entries.Enqueue(entry);
        try { writeLine?.Invoke(entry); }
        catch { /* Late cleanup must not throw through an expired test context. */ }
    }

    public string Describe() => string.Join(Environment.NewLine, _entries.ToArray());
}
