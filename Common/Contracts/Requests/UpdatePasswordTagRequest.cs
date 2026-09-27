using static PasswordManagerLocal.Common.Contracts.Validation.DataValidation;

namespace PasswordManagerLocal.Common.Contracts.Requests;

public sealed class UpdatePasswordTagRequest
{
    public required Guid Id { get; set; }
    public string? Name { get; set; } = null;
    public string? Color { get; set; } = null;

    public bool Validate(out List<string> errors)
    {
        errors = new List<string>();

        var nameEmpty = Name is null;
        var colorEmpty = Color is null;

        if (Id == Guid.Empty)
            errors.Add("Id");

        if (nameEmpty && colorEmpty)
            errors.Add("NoDataUpdate");

        if (!nameEmpty && !IsValidPasswordTagName(Name!))
            errors.Add("Name");

        if (!colorEmpty && (string.IsNullOrWhiteSpace(Color) || !IsValidARGBColor(Color)))
            errors.Add("Color");

        return errors.Count == 0;
    }
}
