namespace PasswordManagerLocal.Common.Backend.Constants;

public static class CryptographyConstants
{
    public const int Sha256HashSizeInBytes = 32;
    public const int Aes256KeySizeInBytes = 32;
    public const int AesGcmNonceSizeInBytes = 12;
    public const int AesGcmTagSizeInBytes = 16;
    public const int DefaultAesFrameSizeBytes = 64 * 1024;
    public const int MaxAesFrameSizeBytes = 1024 * 1024;
}
