using PasswordManagerLocal.Common.Contracts.Constants;
using static PasswordManagerLocal.Common.Contracts.Validation.DataValidation;
using static PasswordManagerLocal.Common.Contracts.Constants.PasswordConstants;

namespace PasswordManagerLocal.Common.Contracts.Requests;

public sealed class NewPasswordTagRequest
{
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = DefaultPasswordColor;

    public bool Validate(out List<string> errors)
    {
        errors = new List<string>();

        if (!IsValidPasswordTagName(Name))
            errors.Add("Name");

        if (string.IsNullOrWhiteSpace(Color) || !IsValidARGBColor(Color))
            errors.Add("Color");

        return errors.Count == 0;
    }
}
