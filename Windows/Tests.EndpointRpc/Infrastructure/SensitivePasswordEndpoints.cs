namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public sealed class SensitivePasswordEndpoints : ThrowingRecordingEndpoints
{
    public SensitivePasswordEndpoints()
        : this(EndpointRpcTestData.Password())
    {
    }

    public SensitivePasswordEndpoints(byte[] passwordBuffer) =>
        PasswordBuffer = passwordBuffer ?? throw new ArgumentNullException(nameof(passwordBuffer));

    public byte[] PasswordBuffer { get; }

    public override Task<byte[]> GetUnsecurePasswordAsync(
        Guid token,
        Guid passwordId,
        CancellationToken ct = default) =>
        Task.FromResult(PasswordBuffer);
}
