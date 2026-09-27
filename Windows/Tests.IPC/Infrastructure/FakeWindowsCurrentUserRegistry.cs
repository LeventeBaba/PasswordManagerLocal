using PasswordManagerLocal.Windows.Agent.Background;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeWindowsCurrentUserRegistry : IWindowsCurrentUserRegistry
{
    private readonly Dictionary<(string KeyPath, string ValueName), object> _values = new();

    public Exception? ReadFailure { get; set; }
    public Exception? WriteFailure { get; set; }
    public Exception? DeleteFailure { get; set; }
    public int ReadCount { get; private set; }
    public int WriteCount { get; private set; }
    public int DeleteCount { get; private set; }
    public string? LastKeyPath { get; private set; }
    public string? LastValueName { get; private set; }
    public string? LastValue { get; private set; }

    public WindowsCurrentUserRegistryValue ReadValue(string keyPath, string valueName)
    {
        ReadCount++;
        LastKeyPath = keyPath;
        LastValueName = valueName;
        if (ReadFailure is not null)
            throw ReadFailure;
        return _values.TryGetValue((keyPath, valueName), out var value)
            ? new WindowsCurrentUserRegistryValue(true, value as string)
            : new WindowsCurrentUserRegistryValue(false, null);
    }

    public void WriteString(string keyPath, string valueName, string value)
    {
        WriteCount++;
        LastKeyPath = keyPath;
        LastValueName = valueName;
        LastValue = value;
        if (WriteFailure is not null)
            throw WriteFailure;
        _values[(keyPath, valueName)] = value;
    }

    public void DeleteValue(string keyPath, string valueName)
    {
        DeleteCount++;
        LastKeyPath = keyPath;
        LastValueName = valueName;
        if (DeleteFailure is not null)
            throw DeleteFailure;
        _values.Remove((keyPath, valueName));
    }

    public void SetValue(string keyPath, string valueName, object value) =>
        _values[(keyPath, valueName)] = value;

    public int ValueCount => _values.Count;
}
