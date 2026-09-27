namespace PasswordManagerLocal.Windows.Agent.Background;

public sealed class WindowsRunStartupRegistration : IWindowsStartupRegistration
{
    private readonly WindowsAgentStartupCommand _startupCommand;
    private readonly IWindowsCurrentUserRegistry _registry;

    public WindowsRunStartupRegistration(
        WindowsAgentStartupCommand startupCommand,
        IWindowsCurrentUserRegistry? registry = null)
    {
        _startupCommand = startupCommand ?? throw new ArgumentNullException(nameof(startupCommand));
        _registry = registry ?? new WindowsCurrentUserRegistry();
    }

    public Task<WindowsStartupRegistrationSnapshot> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = _registry.ReadValue(
            WindowsStartupRegistrationConstants.RunKeyPath,
            WindowsStartupRegistrationConstants.ValueName);
        return Task.FromResult(CreateSnapshot(value));
    }

    public Task RegisterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _registry.WriteString(
            WindowsStartupRegistrationConstants.RunKeyPath,
            WindowsStartupRegistrationConstants.ValueName,
            _startupCommand.Command);
        return Task.CompletedTask;
    }

    public Task UnregisterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _registry.DeleteValue(
            WindowsStartupRegistrationConstants.RunKeyPath,
            WindowsStartupRegistrationConstants.ValueName);
        return Task.CompletedTask;
    }

    public Task RestoreAsync(
        WindowsStartupRegistrationSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        if (!snapshot.EntryExists || snapshot.Command is null)
            return UnregisterAsync(cancellationToken);

        _registry.WriteString(
            WindowsStartupRegistrationConstants.RunKeyPath,
            WindowsStartupRegistrationConstants.ValueName,
            snapshot.Command);
        return Task.CompletedTask;
    }

    private WindowsStartupRegistrationSnapshot CreateSnapshot(
        WindowsCurrentUserRegistryValue value) => new(
            value.EntryExists,
            IsRegistered: value.EntryExists && string.Equals(
                value.StringValue,
                _startupCommand.Command,
                StringComparison.OrdinalIgnoreCase),
            Command: value.StringValue);
}
