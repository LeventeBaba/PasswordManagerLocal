namespace PasswordManagerLocal.Common.Contracts.Errors;

public sealed class LimitReachedException : Exception
{
    public int Limit { get; private set; }
    public string Type { get; private set; }

    public LimitReachedException(int limit, string type) : base($"Limit has been reached for {type}. Limit: {limit}.")
    {
        Limit = limit;
        Type = type;
    }

    public LimitReachedException(int limit) : this(limit, "undefinedType") { }
}
