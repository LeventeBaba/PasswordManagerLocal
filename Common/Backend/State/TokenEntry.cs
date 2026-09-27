namespace PasswordManagerLocal.Common.Backend.State;

internal readonly struct TokenEntry
{
    public readonly Guid Uid;
    public readonly long ExpiresTicksUtc;

    public TokenEntry(Guid uid, long expiresTicksUtc)
    {
        Uid = uid;
        ExpiresTicksUtc = expiresTicksUtc;
    }
}
