using PasswordManagerLocal.Common.Contracts.Constants;
using static PasswordManagerLocal.Common.Contracts.Constants.PasswordConstants;

namespace PasswordManagerLocal.Common.Contracts.Requests;

public sealed class ExportPasswordTagsToUserRequest
{
    public Guid TargetToken { get; set; }
    public IReadOnlyList<Guid>? PasswordTagIds { get; set; } = [];
    public bool DeleteOriginal { get; set; }

    public bool Validate(out List<string> errors)
    {
        errors = new List<string>();

        if (TargetToken == Guid.Empty)
            errors.Add("TargetToken");

        if (PasswordTagIds is null
            || PasswordTagIds.Count == 0
            || PasswordTagIds.Count > MaxNumberOfPasswordTags
            || PasswordTagIds.Any(id => id == Guid.Empty))
        {
            errors.Add("PasswordTagIds");
        }
        else if (PasswordTagIds.Distinct().Count() != PasswordTagIds.Count)
        {
            errors.Add("PasswordTagIds");
        }

        return errors.Count == 0;
    }
}
