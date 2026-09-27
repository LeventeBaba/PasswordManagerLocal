using static PasswordManagerLocal.Common.Contracts.Validation.DataValidation;

namespace PasswordManagerLocal.Common.Contracts.Requests;

public sealed class  UpdateUserProfileRequest
{
    public required Guid Token { get; set; }
    public string? NewEamil { get; set; } = null;
    public string? newFirstName { get; set; } = null;
    public string? NewLastName { get; set; } = null;


    public bool Validate(out List<string> errors)
    {
        errors = new List<string>();

        var emailEmpty = NewEamil is null;
        var firstNameEmpty = newFirstName is null;
        var lastNameEmpty = NewLastName is null;

        if (emailEmpty && firstNameEmpty && lastNameEmpty)
            errors.Add("NoDataUpdate");

        if (!emailEmpty && (string.IsNullOrWhiteSpace(NewEamil) || !IsValidEmail(NewEamil)))
            errors.Add("Email");

        if (!firstNameEmpty && (string.IsNullOrWhiteSpace(newFirstName) || !IsValidFirstName(newFirstName)))
            errors.Add("FirstName");

        if (!lastNameEmpty && (string.IsNullOrWhiteSpace(NewLastName) || !IsValidLastName(NewLastName)))
            errors.Add("LastName");

        return errors.Count == 0;
    }
}
