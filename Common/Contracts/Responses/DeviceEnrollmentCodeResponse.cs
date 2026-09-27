namespace PasswordManagerLocal.Common.Contracts.Responses;

public sealed class DeviceEnrollmentCodeResponse
{
    public string Code { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
}
