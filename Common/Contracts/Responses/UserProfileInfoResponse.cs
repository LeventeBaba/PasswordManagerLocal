using PasswordManagerLocal.Common.Contracts.Devices;

namespace PasswordManagerLocal.Common.Contracts.Responses;

public sealed class UserProfileInfoResponse
{
    public Guid UId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTime RegistrationDate { get; set; }
    public string RegistrationTimeZoneId { get; set; } = string.Empty;
    public DeviceType RegistrationDeviceType { get; set; }
    public bool IsRememberMeEnabled { get; set; }
}
