using static PasswordManagerLocal.Common.Contracts.Validation.DataValidation;

namespace PasswordManagerLocal.Common.Contracts.Requests;

public sealed class NewCustomUserColorRequest
{
    public string? ColorName { get; set; } = null;
    public string ColorCode { get; set; } = string.Empty;

    public bool Validate(out List<string> errors)
    {
        errors = new List<string>();

        if (!IsValidCustomUserColorName(ColorName))
            errors.Add("ColorName");

        if (string.IsNullOrWhiteSpace(ColorCode) || !IsValidARGBColor(ColorCode))
            errors.Add("ColorCode");

        return errors.Count == 0;
    }
}
