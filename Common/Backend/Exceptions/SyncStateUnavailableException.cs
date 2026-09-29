namespace PasswordManagerLocal.Common.Backend.Exceptions;

/// <summary>Local state changed or is unavailable; never evidence of peer misconduct.</summary>
public sealed class SyncStateUnavailableException(string reason, Exception? inner = null) : Exception(reason, inner);
