using static PasswordManagerLocal.Common.Contracts.Validation.DataValidation;

namespace PasswordManagerLocal.Common.Contracts.Requests;

public sealed class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public byte[] Password { get; set; } = [];
    public bool RememberMe { get; set; } = false;


    public bool Validate() => IsValidUsername(Username) && IsValidPassword(Password);
}
