using static PasswordManagerLocal.Common.Contracts.Validation.DataValidation;

namespace PasswordManagerLocal.Common.Contracts.Requests;

public sealed class UpdateCustomUserColorRequest
{
    public required Guid Id { get; set; }
    public string? ColorName { get; set; } = null;
    public bool ClearColorName { get; set; } = false;
    public string? ColorCode { get; set; } = null;

    public bool Validate(out List<string> errors)
    {
        errors = new List<string>();

        var colorNameEmpty = ColorName is null && !ClearColorName;
        var colorCodeEmpty = ColorCode is null;

        if (Id == Guid.Empty)
            errors.Add("Id");

        if (colorNameEmpty && colorCodeEmpty)
            errors.Add("NoDataUpdate");

        if (ColorName is not null && !IsValidCustomUserColorName(ColorName))
            errors.Add("ColorName");

        if (!colorCodeEmpty && (string.IsNullOrWhiteSpace(ColorCode) || !IsValidARGBColor(ColorCode)))
            errors.Add("ColorCode");

        return errors.Count == 0;
    }
}
