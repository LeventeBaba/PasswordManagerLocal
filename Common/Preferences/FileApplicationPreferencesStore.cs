using PasswordManagerLocal.Common.Contracts.Constants;
using PasswordManagerLocal.Common.Contracts.Preferences;
using System.Text.Json;

namespace PasswordManagerLocal.Common.Preferences;

public sealed class FileApplicationPreferencesStore : IApplicationPreferencesStore
{
    private readonly string _preferencesPath;
    private readonly string _lockPath;
    private readonly IApplicationPreferencesFileSystem _fileSystem;
    private readonly IApplicationPreferencesDiagnostics _diagnostics;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileApplicationPreferencesStore(
        string applicationDataDirectory,
        IApplicationPreferencesFileSystem? fileSystem = null,
        IApplicationPreferencesDiagnostics? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        var fullDirectory = Path.GetFullPath(applicationDataDirectory);
        _preferencesPath = Path.Combine(
            fullDirectory,
            ApplicationFileNames.ApplicationPreferencesFileName);
        _lockPath = $"{_preferencesPath}.lock";
        _fileSystem = fileSystem ?? new PhysicalApplicationPreferencesFileSystem();
        _diagnostics = diagnostics ?? new TraceApplicationPreferencesDiagnostics();
    }

    public async Task<ApplicationPreferences> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_preferencesPath)!;
            _fileSystem.CreateDirectory(directory);
            await using var fileLock = await _fileSystem.AcquireExclusiveLockAsync(
                _lockPath,
                cancellationToken).ConfigureAwait(false);

            if (!_fileSystem.FileExists(_preferencesPath))
                return await CreateAndPersistDefaultsAsync("create-defaults", cancellationToken).ConfigureAwait(false);

            try
            {
                var json = await _fileSystem.ReadAllTextAsync(
                    _preferencesPath,
                    cancellationToken).ConfigureAwait(false);
                if (TryParse(json, out var preferences))
                    return preferences;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverableFileFailure(exception))
            {
                _diagnostics.ReportFailure("read", exception);
            }

            return await CreateAndPersistDefaultsAsync("reset-invalid", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableFileFailure(exception))
        {
            _diagnostics.ReportFailure("read-unavailable", exception);
            return ApplicationPreferencesDefaults.Create();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(
        ApplicationPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        Validate(preferences);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_preferencesPath)!;
            _fileSystem.CreateDirectory(directory);
            await using var fileLock = await _fileSystem.AcquireExclusiveLockAsync(
                _lockPath,
                cancellationToken).ConfigureAwait(false);
            await WriteCoreAsync(preferences, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _diagnostics.ReportFailure("write", exception);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ApplicationPreferences> CreateAndPersistDefaultsAsync(
        string operation,
        CancellationToken cancellationToken)
    {
        var defaults = ApplicationPreferencesDefaults.Create();
        try
        {
            await WriteCoreAsync(defaults, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _diagnostics.ReportFailure(operation, exception);
        }

        return defaults;
    }

    private async Task WriteCoreAsync(
        ApplicationPreferences preferences,
        CancellationToken cancellationToken)
    {
        Validate(preferences);
        var directory = Path.GetDirectoryName(_preferencesPath)!;
        var temporaryPath = Path.Combine(
            directory,
            $"{Path.GetFileName(_preferencesPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var json = JsonSerializer.Serialize(
                preferences,
                ApplicationPreferencesJsonContext.Default.ApplicationPreferences);
            await _fileSystem.WriteAllTextAsync(
                temporaryPath,
                json,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _fileSystem.ReplaceFile(temporaryPath, _preferencesPath);
        }
        finally
        {
            try
            {
                if (_fileSystem.FileExists(temporaryPath))
                    _fileSystem.DeleteFile(temporaryPath);
            }
            catch (Exception exception) when (IsRecoverableFileFailure(exception))
            {
                _diagnostics.ReportFailure("cleanup-temporary", exception);
            }
        }
    }

    private static bool TryParse(
        string json,
        out ApplicationPreferences preferences)
    {
        preferences = null!;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Count() != 3 ||
                !root.TryGetProperty("schemaVersion", out var schemaVersionElement) ||
                schemaVersionElement.ValueKind != JsonValueKind.Number ||
                !schemaVersionElement.TryGetInt32(out var schemaVersion) ||
                schemaVersion != ApplicationPreferences.CurrentSchemaVersion ||
                !root.TryGetProperty("language", out var languageElement) ||
                languageElement.ValueKind != JsonValueKind.String ||
                !TryParseLanguage(languageElement.GetString(), out var language) ||
                !root.TryGetProperty("theme", out var themeElement) ||
                themeElement.ValueKind != JsonValueKind.String ||
                !TryParseTheme(themeElement.GetString(), out var theme))
            {
                return false;
            }

            preferences = new ApplicationPreferences
            {
                SchemaVersion = schemaVersion,
                Language = language,
                Theme = theme
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseLanguage(string? value, out AppLanguage language)
    {
        if (string.Equals(value, nameof(AppLanguage.English), StringComparison.OrdinalIgnoreCase))
        {
            language = AppLanguage.English;
            return true;
        }

        if (string.Equals(value, nameof(AppLanguage.Hungarian), StringComparison.OrdinalIgnoreCase))
        {
            language = AppLanguage.Hungarian;
            return true;
        }

        language = default;
        return false;
    }

    private static bool TryParseTheme(string? value, out AppThemeMode theme)
    {
        if (string.Equals(value, nameof(AppThemeMode.Light), StringComparison.OrdinalIgnoreCase))
        {
            theme = AppThemeMode.Light;
            return true;
        }

        if (string.Equals(value, nameof(AppThemeMode.Dark), StringComparison.OrdinalIgnoreCase))
        {
            theme = AppThemeMode.Dark;
            return true;
        }

        theme = default;
        return false;
    }

    private static void Validate(ApplicationPreferences preferences)
    {
        if (preferences.SchemaVersion != ApplicationPreferences.CurrentSchemaVersion)
            throw new ArgumentOutOfRangeException(nameof(preferences), "The application-preference schema version is unsupported.");
        if (preferences.Language is not AppLanguage.English and not AppLanguage.Hungarian)
            throw new ArgumentOutOfRangeException(nameof(preferences), "The application language is invalid.");
        if (preferences.Theme is not AppThemeMode.Light and not AppThemeMode.Dark)
            throw new ArgumentOutOfRangeException(nameof(preferences), "The application theme is invalid.");
    }

    private static bool IsRecoverableFileFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
