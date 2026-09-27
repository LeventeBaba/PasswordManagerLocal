using Android.OS;
using Android.Runtime;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using System;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Android.Backend.Security;

public sealed class AndroidKeyProtector : IKeyProtector
{
    private const string AndroidKeyStoreProvider = "AndroidKeyStore";
    private const string KeyAlias = "PasswordManagerLocal.DbMasterKey.AesGcm";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int AuthenticationTagBits = 128;
    private static readonly byte[] CurrentBlobHeader = { 80, 77, 76, 65, 75, 80, 49 };

    private readonly ISecretKey _key;

    public AndroidKeyProtector()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.M)
            throw new PlatformNotSupportedException("Android Keystore AES-GCM protection requires Android 6.0 (API 23) or newer.");

        try
        {
            _key = GetOrCreateSecretKey();
        }
        catch (Exception exception) when (IsDeviceLockedFailure(exception))
        {
            throw CreateDeviceLockedException(exception);
        }
    }


    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        try
        {
            var cipher = Cipher.GetInstance(Transformation)
                         ?? throw new CryptographicException("Failed to create Android Keystore cipher.");

            cipher.Init(Javax.Crypto.CipherMode.EncryptMode, _key);

            var iv = cipher.GetIV() ?? throw new CryptographicException("Android Keystore cipher did not generate an IV.");
            var ciphertext = cipher.DoFinal(plaintext.ToArray())
                             ?? throw new CryptographicException("Android Keystore encryption failed.");

            return PackCurrentBlob(iv, ciphertext);
        }
        catch (GeneralSecurityException exception) when (IsDeviceLockedFailure(exception))
        {
            throw CreateDeviceLockedException(exception);
        }
        catch (GeneralSecurityException exception)
        {
            throw new CryptographicException("Android Keystore encryption failed.", exception);
        }
    }


    public byte[] Unprotect(ReadOnlySpan<byte> protectedBlob)
    {
        var data = protectedBlob.ToArray();

        if (!IsCurrentBlob(data))
            throw new CryptographicException("Unsupported Android protected payload format.");

        return UnprotectCurrent(data);
    }


    private static ISecretKey GetOrCreateSecretKey()
    {
        var keyStore = KeyStore.GetInstance(AndroidKeyStoreProvider)
                       ?? throw new CryptographicException("Failed to open Android Keystore.");

        keyStore.Load(null);

        if (keyStore.ContainsAlias(KeyAlias))
        {
            var existingKey = keyStore.GetKey(KeyAlias, null)?.JavaCast<ISecretKey>();
            return existingKey ?? throw new CryptographicException("Android Keystore entry is not an AES secret key.");
        }

        var keyGenerator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, AndroidKeyStoreProvider)
                           ?? throw new CryptographicException("Failed to create Android Keystore AES key generator.");

        var builder = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetKeySize(256)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetRandomizedEncryptionRequired(true);

        if (OperatingSystem.IsAndroidVersionAtLeast(28))
            builder.SetUnlockedDeviceRequired(true);

        keyGenerator.Init(builder.Build());

        var generatedKey = keyGenerator.GenerateKey();
        return generatedKey ?? throw new CryptographicException("Failed to generate Android Keystore AES key.");
    }


    private static byte[] PackCurrentBlob(byte[] iv, byte[] ciphertext)
    {
        if (iv.Length > byte.MaxValue)
            throw new CryptographicException("Android Keystore IV is too large.");

        var result = new byte[CurrentBlobHeader.Length + 1 + iv.Length + ciphertext.Length];
        var offset = 0;

        Buffer.BlockCopy(CurrentBlobHeader, 0, result, offset, CurrentBlobHeader.Length);
        offset += CurrentBlobHeader.Length;

        result[offset++] = (byte)iv.Length;

        Buffer.BlockCopy(iv, 0, result, offset, iv.Length);
        offset += iv.Length;

        Buffer.BlockCopy(ciphertext, 0, result, offset, ciphertext.Length);

        return result;
    }


    private byte[] UnprotectCurrent(byte[] data)
    {
        var offset = CurrentBlobHeader.Length;

        if (data.Length < offset + 2)
            throw new CryptographicException("Invalid Android protected payload.");

        var ivLength = data[offset++];

        if (ivLength <= 0 || data.Length <= offset + ivLength)
            throw new CryptographicException("Invalid Android protected payload IV.");

        var iv = new byte[ivLength];
        Buffer.BlockCopy(data, offset, iv, 0, ivLength);
        offset += ivLength;

        var ciphertextLength = data.Length - offset;

        if (ciphertextLength < AuthenticationTagBits / 8)
            throw new CryptographicException("Invalid Android protected payload ciphertext.");

        var ciphertext = new byte[ciphertextLength];
        Buffer.BlockCopy(data, offset, ciphertext, 0, ciphertextLength);

        try
        {
            var cipher = Cipher.GetInstance(Transformation)
                         ?? throw new CryptographicException("Failed to create Android Keystore cipher.");

            cipher.Init(Javax.Crypto.CipherMode.DecryptMode, _key, new GCMParameterSpec(AuthenticationTagBits, iv));

            return cipher.DoFinal(ciphertext)
                   ?? throw new CryptographicException("Android Keystore decryption failed.");
        }
        catch (GeneralSecurityException exception) when (IsDeviceLockedFailure(exception))
        {
            throw CreateDeviceLockedException(exception);
        }
        catch (GeneralSecurityException exception)
        {
            throw new CryptographicException("Android Keystore decryption failed.", exception);
        }
    }


    private static KeyProtectorUnavailableException CreateDeviceLockedException(Exception exception) =>
        new(KeyProtectorUnavailableReason.DeviceLocked, exception);


    private static bool IsDeviceLockedFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is UserNotAuthenticatedException)
                return true;

            var message = current.Message;
            if (message.Contains("user not authenticated", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("device is locked", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("device locked", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }


    private static bool IsCurrentBlob(byte[] data)
    {
        if (data.Length < CurrentBlobHeader.Length)
            return false;

        for (var i = 0; i < CurrentBlobHeader.Length; i++)
        {
            if (data[i] != CurrentBlobHeader[i])
                return false;
        }

        return true;
    }
}