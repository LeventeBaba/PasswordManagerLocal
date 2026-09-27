using PasswordManagerLocal.Common.Frontend.Services;
using System;
using System.Collections.Generic;

namespace PasswordManagerLocal.Common.Frontend.Abstractions.Services;

public interface IAuthSessionRegistry
{
    Guid CurrentUserToken { get; set; }
    bool TryAdd(Guid token, bool select = true);
    bool TrySetProfile(Guid token, Guid userId, string displayName, string subtitle, string username, string email, bool isRememberMeEnabled);
    bool TrySetRememberMe(Guid token, bool isRememberMeEnabled);
    bool TryReplaceToken(Guid oldToken, Guid newToken);
    bool TryRemove(Guid token);
    bool ContainsUserId(Guid userId, Guid excludedToken = default);
    AuthSessionProfile? GetSession(Guid token);
    IReadOnlyList<Guid> ListTokens();
    IReadOnlyList<AuthSessionProfile> ListSessions();
}
