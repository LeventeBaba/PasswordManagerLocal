using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using System.Text.Json;

namespace PasswordManagerLocal.Common.Backend.Configuration.BackgroundSync;

public sealed class FileBackgroundSyncSettingsStore : IBackgroundSyncSettingsStore
{
    private readonly string _settingsPath;
    private readonly IBackgroundSyncSettingsFileSystem _fileSystem;
    private readonly object _gate = new();

    public FileBackgroundSyncSettingsStore(
        string applicationDataDirectory,
        IBackgroundSyncSettingsFileSystem? fileSystem = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        _settingsPath = Path.Combine(
            Path.GetFullPath(applicationDataDirectory),
            ApplicationFileNames.BackgroundSyncSettingsFileName);
        _fileSystem = fileSystem ?? new PhysicalBackgroundSyncSettingsFileSystem();
    }

    public Task<BackgroundSyncSettings> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_fileSystem.FileExists(_settingsPath))
                return Task.FromResult(new BackgroundSyncSettings(false));

            try
            {
                var json = _fileSystem.ReadAllText(_settingsPath);
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("isEnabled", out var enabled) ||
                    enabled.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                {
                    throw new InvalidDataException(
                        "The background synchronization settings file is semantically invalid.");
                }

                return Task.FromResult(new BackgroundSyncSettings(enabled.GetBoolean()));
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                throw new InvalidDataException(
                    "The background synchronization settings file could not be read safely.",
                    exception);
            }
        }
    }

    public async Task WriteAsync(
        BackgroundSyncSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(_settingsPath)!;
        _fileSystem.CreateDirectory(directory);
        await using var fileLock = await _fileSystem.AcquireExclusiveLockAsync(
            $"{_settingsPath}.lock", cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var temporaryPath = $"{_settingsPath}.tmp";

            // The exclusive file lock makes a fixed staging slot safe after a crash.
            // Failure to reclaim an orphan stops the write before allocating another file.
            if (_fileSystem.FileExists(temporaryPath))
                _fileSystem.DeleteFile(temporaryPath);
            var name = Path.GetFileName(_settingsPath);
            foreach (var candidate in _fileSystem.EnumerateFiles(directory, $"{name}.*.tmp"))
            {
                var suffix = Path.GetFileName(candidate).AsSpan(name.Length + 1);
                if (suffix.Length == 36 && Guid.TryParseExact(suffix[..32], "N", out _))
                    _fileSystem.DeleteFile(candidate);
            }

            try
            {
                var json = JsonSerializer.Serialize(
                    settings,
                    BackgroundSyncSettingsJsonContext.Default.BackgroundSyncSettings);
                _fileSystem.WriteAllText(temporaryPath, json);
                cancellationToken.ThrowIfCancellationRequested();
                _fileSystem.ReplaceFile(temporaryPath, _settingsPath);
            }
            finally
            {
                try
                {
                    if (_fileSystem.FileExists(temporaryPath))
                        _fileSystem.DeleteFile(temporaryPath);
                }
                catch
                {
                }
            }
        }
    }
}
