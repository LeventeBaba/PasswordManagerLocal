namespace PasswordManagerLocal.Common.Backend.Abstractions.Persistence;

internal sealed class StorageWriteException(
    StorageWriteException.FailureKind kind,
    Type? rowType = null,
    Exception? innerException = null)
    : InvalidOperationException($"The database write failed ({kind}).", innerException)
{
    public enum FailureKind
    {
        StaleRow,
        UniqueKeyConflict,
        Busy
    }

    public FailureKind Kind { get; } = kind;
    public Type? RowType { get; } = rowType;
}
