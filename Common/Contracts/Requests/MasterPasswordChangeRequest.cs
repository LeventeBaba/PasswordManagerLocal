using static PasswordManagerLocal.Common.Contracts.Validation.DataValidation;

namespace PasswordManagerLocal.Common.Contracts.Requests;

public sealed class MasterPasswordChangeRequest
{
    public required Guid Token { get; set; }
    public required byte[] Password { get; set; }
    public required byte[] NewPassword { get; set; }


    public bool Validate(out List<string> errors)
    {
        errors = new List<string>();

        if (!IsValidPassword(Password))
            errors.Add("CurrentPassword");

        if (!IsValidPassword(NewPassword))
            errors.Add("NewPassword");

        return errors.Count == 0;
    }
}
