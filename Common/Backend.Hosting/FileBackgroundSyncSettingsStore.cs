using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using System.Text.Json;

namespace PasswordManagerLocal.Common.Backend.Hosting;

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

    public Task WriteAsync(
        BackgroundSyncSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(_settingsPath)!;
            var temporaryPath = Path.Combine(
                directory,
                $"{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                _fileSystem.CreateDirectory(directory);
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

        return Task.CompletedTask;
    }
}
