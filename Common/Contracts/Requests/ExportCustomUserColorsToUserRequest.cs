using PasswordManagerLocal.Common.Contracts.Constants;
using static PasswordManagerLocal.Common.Contracts.Constants.PasswordConstants;

namespace PasswordManagerLocal.Common.Contracts.Requests;

public sealed class ExportCustomUserColorsToUserRequest
{
    public Guid TargetToken { get; set; }
    public IReadOnlyList<Guid>? CustomUserColorIds { get; set; } = [];
    public bool DeleteOriginal { get; set; }

    public bool Validate(out List<string> errors)
    {
        errors = new List<string>();

        if (TargetToken == Guid.Empty)
            errors.Add("TargetToken");

        if (CustomUserColorIds is null
            || CustomUserColorIds.Count == 0
            || CustomUserColorIds.Count > MaxNumberOfCustomUserColors
            || CustomUserColorIds.Any(id => id == Guid.Empty))
        {
            errors.Add("CustomUserColorIds");
        }
        else if (CustomUserColorIds.Distinct().Count() != CustomUserColorIds.Count)
        {
            errors.Add("CustomUserColorIds");
        }

        return errors.Count == 0;
    }
}
