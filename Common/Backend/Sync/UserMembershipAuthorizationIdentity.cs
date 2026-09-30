using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Sync;

public static class UserMembershipAuthorizationIdentity
{
    public static Guid GetCanonicalAuthorizationId(UserMembershipAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        if (!authorization.IsGenesis && authorization.AdditionOperationId is Guid additionOperationId && additionOperationId != Guid.Empty)
            return additionOperationId;

        return authorization.AuthorizationId;
    }
}
