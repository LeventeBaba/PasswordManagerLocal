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
        if (File.Exists(paths.DatabasePath))
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
        if (protectedBlob.Length == 0)
            throw new DatabaseConfigurationInvalidException("The protected database key is missing.");

        var header = CreateHeader();
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
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

            File.Move(temporaryPath, path, true);
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
        var fileBytes = File.ReadAllBytes(path);

        try
        {
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

}
