using PasswordManagerLocal.Common.Contracts.Constants;
using static PasswordManagerLocal.Common.Contracts.Validation.DataValidation;
using static PasswordManagerLocal.Common.Contracts.Constants.PasswordConstants;

namespace PasswordManagerLocal.Common.Contracts.Requests;

public sealed class NewPasswordRequest
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Color { get; set; } = DefaultPasswordColor;
    public byte[] Password { get; set; } = [];
    public List<Guid>? TagIds { get; set; } = [];



    public bool Validate(out List<string> errors)
    {
        errors = new List<string>();

        if (!IsValidPasswordName(Name))
            errors.Add("Name");

        if (!IsValidDescription(Description))
            errors.Add("Description");

        if (!IsValidARGBColor(Color))
            errors.Add("Color");

        if (!IsValidPassword(Password))
            errors.Add("Password");

        if (TagIds is null || TagIds.Any(id => id == Guid.Empty) || TagIds.Distinct().Count() != TagIds.Count)
            errors.Add("TagIds");

        return errors.Count == 0;
    }
}
