using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Configuration;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Exceptions;
using System.Buffers.Binary;
using System.Security.Cryptography;
using static PasswordManagerLocal.Common.Backend.Constants.DatabaseConstants;

namespace PasswordManagerLocal.Common.Backend.Security;

internal static class DbConfigManager
{
    internal static string GetOrCreateDatabasePassword(
        BackendStoragePaths paths,
        IKeyProtector protector)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(protector);

        using var fileLock = StorageFileLock.Acquire($"{paths.DatabaseConfigPath}.lock");

        if (Path.Exists(paths.DatabaseResetPendingPath))
            throw new DatabaseResetIncompleteException();

        if (!File.Exists(paths.DatabaseConfigPath))
            CreateDbConfig(paths, protector);

        var protectedBlob = ReadAndValidateDbConfig(paths.DatabaseConfigPath);
        try
        {
            byte[] keyBytes;
            try
            {
                keyBytes = protector.Unprotect(protectedBlob);
            }
            catch (KeyProtectorUnavailableException)
            {
                throw;
            }
            catch (Exception exception) when (exception is CryptographicException or InvalidDataException)
            {
                throw new DatabaseKeyUnavailableException(exception);
            }

            try
            {
                if (keyBytes.Length != CryptographyConstants.Aes256KeySizeInBytes)
                    throw new DatabaseKeyUnavailableException();

                return Convert.ToBase64String(keyBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(keyBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBlob);
        }
    }

    private static void CreateDbConfig(BackendStoragePaths paths, IKeyProtector protector)
    {
        if (new[] { paths.DatabasePath, paths.DatabaseMigrationBackupPath, paths.DatabaseMigrationTempPath }
            .Any(path => File.Exists(path) || File.Exists($"{path}-wal") ||
                         File.Exists($"{path}-shm") || File.Exists($"{path}-journal")))
            throw new DatabaseConfigurationInvalidException("The configuration file is missing for an existing database.");

        using var key = EncryptionKey.Create();
        var keyBytes = key.ExportCopy();
        try
        {
            var protectedBlob = protector.Protect(keyBytes);
            try
            {
                WriteDbConfig(paths.DatabaseConfigPath, protectedBlob);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBlob);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    private static void WriteDbConfig(string path, byte[] protectedBlob)
    {
        if (protectedBlob.Length == 0 ||
            protectedBlob.Length > StorageCapacityConstants.MaxDatabaseConfigurationBytes - DbConfigHeaderLength)
            throw new DatabaseConfigurationInvalidException("The protected database key size is invalid.");

        var header = CreateHeader();
        var temporaryPath = $"{path}.tmp";

        try
        {
            DeleteTemporaryConfigurations(path);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(header);
                stream.Write(protectedBlob);
                stream.Flush(true);
            }

            // Another initializer cannot overwrite a previously committed key.
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(header);
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static byte[] ReadAndValidateDbConfig(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= DbConfigHeaderLength ||
            stream.Length > StorageCapacityConstants.MaxDatabaseConfigurationBytes)
            throw new DatabaseConfigurationInvalidException("The database configuration size is invalid.");
        var fileBytes = new byte[(int)stream.Length];

        try
        {
            stream.ReadExactly(fileBytes);
            if (fileBytes.Length <= DbConfigHeaderLength)
                throw new DatabaseConfigurationInvalidException("The configuration header or protected key is incomplete.");

            var header = fileBytes.AsSpan(0, DbConfigHeaderLength);
            if (header[0] != DbConfigMagicFirstByte || header[1] != DbConfigMagicSecondByte)
                throw new DatabaseConfigurationInvalidException("The configuration header is invalid.");

            var version = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(DbConfigVersionOffset, sizeof(int)));
            var headerLength = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(DbConfigHeaderLengthOffset, sizeof(int)));

            if (headerLength != DbConfigHeaderLength)
                throw new DatabaseConfigurationInvalidException("The configuration header length is invalid.");

            if (version < OldestSupportedDbVersion || version > CurrentDbVersion)
                throw new DatabaseVersionNotSupportedException(version, OldestSupportedDbVersion, CurrentDbVersion);

            return fileBytes.AsSpan(DbConfigHeaderLength).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileBytes);
        }
    }

    private static byte[] CreateHeader()
    {
        var header = new byte[DbConfigHeaderLength];
        header[0] = DbConfigMagicFirstByte;
        header[1] = DbConfigMagicSecondByte;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(DbConfigVersionOffset, sizeof(int)), CurrentDbVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(DbConfigHeaderLengthOffset, sizeof(int)), DbConfigHeaderLength);
        return header;
    }

    // Call only after the current configuration's key has opened the vault and passed quick_check.
    // Unwrapping a key alone does not prove that an orphan is redundant for database recovery.
    internal static void TryCleanupTemporaryConfigurationsForVerifiedDatabase(BackendStoragePaths paths)
    {
        try
        {
            using var fileLock = StorageFileLock.Acquire($"{paths.DatabaseConfigPath}.lock");
            DeleteTemporaryConfigurations(paths.DatabaseConfigPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Keep a usable vault available; this path never allocates another staging file.
        }
    }

    private static void DeleteTemporaryConfigurations(string path)
    {
        File.Delete($"{path}.tmp");
        var name = Path.GetFileName(path);
        foreach (var candidate in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, $"{name}.*.tmp"))
        {
            var suffix = Path.GetFileName(candidate).AsSpan(name.Length + 1);
            if (suffix.Length == 36 && Guid.TryParseExact(suffix[..32], "N", out _))
                File.Delete(candidate);
        }
    }

}
