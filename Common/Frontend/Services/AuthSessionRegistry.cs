using PasswordManagerLocal.Common.Frontend.Abstractions.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PasswordManagerLocal.Common.Frontend.Services;

public sealed class AuthSessionRegistry : IAuthSessionRegistry, IDisposable
{
    private readonly List<AuthSessionProfile> _sessions = new();
    private Guid _selection = Guid.Empty;
    private bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        _sessions.Clear();
        _selection = Guid.Empty;
    }

    public Guid CurrentUserToken
    {
        get => _selection;
        set
        {
            if (_disposed)
            {
                _selection = Guid.Empty;
                return;
            }

            if (value == Guid.Empty)
            {
                _selection = Guid.Empty;
                return;
            }

            _selection = _sessions.Any(session => session.Token == value)
                ? value
                : Guid.Empty;
        }
    }

    public bool TryAdd(Guid token, bool select = true)
    {
        if (_disposed || token == Guid.Empty)
            return false;

        if (_sessions.All(session => session.Token != token))
            _sessions.Add(new AuthSessionProfile { Token = token });

        if (select)
            CurrentUserToken = token;

        return true;
    }

    public bool TrySetProfile(Guid token, Guid userId, string displayName, string subtitle, string username, string email, bool isRememberMeEnabled)
    {
        if (_disposed || token == Guid.Empty)
            return false;

        var index = _sessions.FindIndex(session => session.Token == token);
        if (index < 0)
            return false;

        _sessions[index] = new AuthSessionProfile
        {
            Token = token,
            UserId = userId,
            DisplayName = displayName,
            Subtitle = subtitle,
            Username = username,
            Email = email,
            IsRememberMeEnabled = isRememberMeEnabled
        };

        return true;
    }

    public bool TrySetRememberMe(Guid token, bool isRememberMeEnabled)
    {
        if (_disposed || token == Guid.Empty)
            return false;

        var index = _sessions.FindIndex(session => session.Token == token);
        if (index < 0)
            return false;

        var session = _sessions[index];
        _sessions[index] = new AuthSessionProfile
        {
            Token = session.Token,
            UserId = session.UserId,
            DisplayName = session.DisplayName,
            Subtitle = session.Subtitle,
            Username = session.Username,
            Email = session.Email,
            IsRememberMeEnabled = isRememberMeEnabled
        };

        return true;
    }

    public bool TryReplaceToken(Guid oldToken, Guid newToken)
    {
        if (_disposed || oldToken == Guid.Empty || newToken == Guid.Empty)
            return false;

        var index = _sessions.FindIndex(session => session.Token == oldToken);
        if (index < 0)
            return false;

        var oldSession = _sessions[index];
        _sessions[index] = new AuthSessionProfile
        {
            Token = newToken,
            UserId = oldSession.UserId,
            DisplayName = oldSession.DisplayName,
            Subtitle = oldSession.Subtitle,
            Username = oldSession.Username,
            Email = oldSession.Email,
            IsRememberMeEnabled = oldSession.IsRememberMeEnabled
        };

        if (_selection == oldToken)
            _selection = newToken;

        return true;
    }

    public bool TryRemove(Guid token)
    {
        if (_disposed || token == Guid.Empty)
            return false;

        var index = _sessions.FindIndex(session => session.Token == token);
        if (index < 0)
            return false;

        var wasSelected = _selection == token;
        _sessions.RemoveAt(index);

        if (wasSelected)
            CurrentUserToken = _sessions.FirstOrDefault()?.Token ?? Guid.Empty;

        return true;
    }

    public bool ContainsUserId(Guid userId, Guid excludedToken = default)
    {
        if (_disposed || userId == Guid.Empty)
            return false;

        return _sessions.Any(session =>
            session.UserId == userId &&
            (excludedToken == Guid.Empty || session.Token != excludedToken));
    }

    public AuthSessionProfile? GetSession(Guid token) =>
        _disposed ? null : _sessions.FirstOrDefault(session => session.Token == token);

    public IReadOnlyList<Guid> ListTokens() =>
        _disposed ? Array.Empty<Guid>() : _sessions.Select(session => session.Token).ToList();

    public IReadOnlyList<AuthSessionProfile> ListSessions() =>
        _disposed ? Array.Empty<AuthSessionProfile>() : _sessions.ToList();
}
