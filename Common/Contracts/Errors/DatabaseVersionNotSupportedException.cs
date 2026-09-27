namespace PasswordManagerLocal.Common.Contracts.Errors;

public sealed class DatabaseVersionNotSupportedException : InvalidOperationException
{
    public DatabaseVersionNotSupportedException(
        int? detectedVersion,
        int oldestSupportedVersion,
        int currentVersion,
        string? reason = null,
        Exception? innerException = null)
        : base(BuildMessage(detectedVersion, oldestSupportedVersion, currentVersion, reason), innerException)
    {
        DetectedVersion = detectedVersion;
        OldestSupportedVersion = oldestSupportedVersion;
        CurrentVersion = currentVersion;
        Reason = reason;
    }

    public int? DetectedVersion { get; }

    public int OldestSupportedVersion { get; }

    public int CurrentVersion { get; }

    public string? Reason { get; }

    private static string BuildMessage(
        int? detectedVersion,
        int oldestSupportedVersion,
        int currentVersion,
        string? reason)
    {
        var detectedText = detectedVersion.HasValue
            ? detectedVersion.Value.ToString()
            : "unknown";

        var message = $"Database version '{detectedText}' is not supported. Supported versions are {oldestSupportedVersion} through {currentVersion}.";
        return string.IsNullOrWhiteSpace(reason) ? message : $"{message} {reason}";
    }
}
