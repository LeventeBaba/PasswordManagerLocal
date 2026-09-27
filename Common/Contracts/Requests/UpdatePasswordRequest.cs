using static PasswordManagerLocal.Common.Contracts.Validation.DataValidation;

namespace PasswordManagerLocal.Common.Contracts.Requests;

public sealed class UpdatePasswordRequest
{
    public required Guid Id { get; set; }
    public string? Name { get; set; } = null;
    public string? Description { get; set; } = null;
    public string? Color { get; set; } = null;
    public byte[]? Password { get; set; } = null;
    public List<Guid>? TagIds { get; set; } = null;



    public bool Validate(out List<string> errors)
    {
        errors = new List<string>();

        var nameEmpty = Name is null;
        var descriptionEmpty = Description is null;
        var colorEmpty = Color is null;
        var passwordEmpty = Password is null;
        var tagIdsEmpty = TagIds is null;

        if (nameEmpty && descriptionEmpty && colorEmpty && passwordEmpty && tagIdsEmpty)
            errors.Add("NoDataUpdate");

        if (!nameEmpty && (string.IsNullOrWhiteSpace(Name) || !IsValidPasswordName(Name)))
            errors.Add("Name");

        if (!descriptionEmpty && !IsValidDescription(Description!))
            errors.Add("Description");

        if (!colorEmpty && (string.IsNullOrWhiteSpace(Color) || !IsValidARGBColor(Color)))
            errors.Add("Color");

        if (!passwordEmpty && (Password is null || !IsValidPassword(Password)))
            errors.Add("Password");

        if (!tagIdsEmpty && (TagIds!.Any(id => id == Guid.Empty) || TagIds.Distinct().Count() != TagIds.Count))
            errors.Add("TagIds");

        return errors.Count == 0;
    }
}
