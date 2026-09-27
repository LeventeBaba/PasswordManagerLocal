namespace PasswordManagerLocal.Common.Backend.Sync.Presence;

public sealed record DevicePresenceProbeResult(
    bool IsSuccess,
    DevicePresenceFailureKind FailureKind = DevicePresenceFailureKind.None)
{
    public static DevicePresenceProbeResult Success { get; } = new(true);

    public static DevicePresenceProbeResult Failed(DevicePresenceFailureKind failureKind) =>
        new(false, failureKind == DevicePresenceFailureKind.None
            ? DevicePresenceFailureKind.InvalidResponse
            : failureKind);
}
