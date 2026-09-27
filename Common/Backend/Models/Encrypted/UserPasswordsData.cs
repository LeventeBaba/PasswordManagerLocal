using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Security;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Backend.Models.Encrypted;

public sealed class UserPasswordsData : IntegrityCheckableBase, IDisposable
{
    private bool _disposed;

    public List<SecurePassword> Passwords { get; set; } = [];
    public List<DeletedPasswordData> DeletedPasswords { get; set; } = [];
    public List<CustomUserColor> CustomColors { get; set; } = [];
    public List<DeletedCustomUserColorData> DeletedCustomColors { get; set; } = [];
    public List<PasswordTag> Tags { get; set; } = [];
    public List<DeletedPasswordTagData> DeletedTags { get; set; } = [];
    public byte[] PasswordKey { get; set; } = [];
    public byte[] PasswordsIntegrityHash { get; set; } = [];
    public byte[] CustomColorsIntegrityHash { get; set; } = [];
    public byte[] PasswordTagsIntegrityHash { get; set; } = [];

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        CryptographicOperations.ZeroMemory(PasswordKey);
        CryptographicOperations.ZeroMemory(PasswordsIntegrityHash);
        CryptographicOperations.ZeroMemory(CustomColorsIntegrityHash);
        CryptographicOperations.ZeroMemory(PasswordTagsIntegrityHash);
        CryptographicOperations.ZeroMemory(IntegrityHash);
        if (disposing)
        {
            Passwords.ForEach(pw => pw.Dispose());
            DeletedPasswords.ForEach(deleted => deleted.Dispose());
            CustomColors.ForEach(color => color.Dispose());
            DeletedCustomColors.ForEach(deleted => deleted.Dispose());
            Tags.ForEach(tag => tag.Dispose());
            DeletedTags.ForEach(deleted => deleted.Dispose());
        }
        Passwords.Clear();
        DeletedPasswords.Clear();
        CustomColors.Clear();
        DeletedCustomColors.Clear();
        Tags.Clear();
        DeletedTags.Clear();

        _disposed = true;
    }

    public override byte[] CalculateIntegrityHash() =>
        Hashing.SHA256Hash(hash =>
        {
            hash.WriteBytes(PasswordsIntegrityHash);
            hash.WriteBytes(CustomColorsIntegrityHash);
            hash.WriteBytes(PasswordTagsIntegrityHash);
        });


    public override bool IsIntegrityValid()
    {
        if (!IsSha256Hash(IntegrityHash) ||
            !IsSha256Hash(PasswordsIntegrityHash) ||
            !IsSha256Hash(CustomColorsIntegrityHash) ||
            !IsSha256Hash(PasswordTagsIntegrityHash))
            return false;

        var calculatedPasswordsIntegrityHash = CalculatePasswordsIntegrityHash();
        var calculatedCustomColorsIntegrityHash = CalculateCustomColorsIntegrityHash();
        var calculatedPasswordTagsIntegrityHash = CalculatePasswordTagsIntegrityHash();

        return Hashing.Verify(PasswordsIntegrityHash, calculatedPasswordsIntegrityHash) &&
               Hashing.Verify(CustomColorsIntegrityHash, calculatedCustomColorsIntegrityHash) &&
               Hashing.Verify(PasswordTagsIntegrityHash, calculatedPasswordTagsIntegrityHash) &&
               Hashing.Verify(IntegrityHash, CalculateIntegrityHash());
    }


    public override void GenerateIntegrityHash()
    {
        PasswordsIntegrityHash = ReplaceHash(PasswordsIntegrityHash, CalculatePasswordsIntegrityHash());
        CustomColorsIntegrityHash = ReplaceHash(CustomColorsIntegrityHash, CalculateCustomColorsIntegrityHash());
        PasswordTagsIntegrityHash = ReplaceHash(PasswordTagsIntegrityHash, CalculatePasswordTagsIntegrityHash());
        GenerateRootIntegrityHash();
    }


    public void GeneratePasswordsIntegrityHash()
    {
        PasswordsIntegrityHash = ReplaceHash(PasswordsIntegrityHash, CalculatePasswordsIntegrityHash());
        GenerateRootIntegrityHash();
    }


    public void GenerateCustomColorsIntegrityHash()
    {
        CustomColorsIntegrityHash = ReplaceHash(CustomColorsIntegrityHash, CalculateCustomColorsIntegrityHash());
        GenerateRootIntegrityHash();
    }


    public void GeneratePasswordTagsIntegrityHash()
    {
        PasswordTagsIntegrityHash = ReplaceHash(PasswordTagsIntegrityHash, CalculatePasswordTagsIntegrityHash());
        GenerateRootIntegrityHash();
    }


    public byte[] CalculatePasswordsIntegrityHash() =>
        Hashing.SHA256Hash(hash =>
        {
            hash.WriteBytes(PasswordKey);
            hash.Write(Passwords.Count);
            foreach (var password in Passwords.OrderBy(password => password.Id))
                hash.WriteBytes(password.IntegrityHash);
            hash.Write(DeletedPasswords.Count);
            foreach (var deleted in DeletedPasswords.OrderBy(deleted => deleted.Id))
                hash.WriteBytes(deleted.IntegrityHash);
        });


    public byte[] CalculateCustomColorsIntegrityHash() =>
        Hashing.SHA256Hash(hash =>
        {
            hash.Write(CustomColors.Count);
            foreach (var color in CustomColors.OrderBy(color => color.Id))
                hash.WriteBytes(color.IntegrityHash);
            hash.Write(DeletedCustomColors.Count);
            foreach (var deleted in DeletedCustomColors.OrderBy(deleted => deleted.Id))
                hash.WriteBytes(deleted.IntegrityHash);
        });


    public byte[] CalculatePasswordTagsIntegrityHash() =>
        Hashing.SHA256Hash(hash =>
        {
            hash.Write(Tags.Count);
            foreach (var tag in Tags.OrderBy(tag => tag.Id))
                hash.WriteBytes(tag.IntegrityHash);
            hash.Write(DeletedTags.Count);
            foreach (var deleted in DeletedTags.OrderBy(deleted => deleted.Id))
                hash.WriteBytes(deleted.IntegrityHash);
        });


    private void GenerateRootIntegrityHash() =>
        base.GenerateIntegrityHash();


    private static bool IsSha256Hash(byte[]? hash) =>
        hash is { Length: CryptographyConstants.Sha256HashSizeInBytes };


    private static byte[] ReplaceHash(byte[]? target, byte[] value)
    {
        if (target is not null)
            CryptographicOperations.ZeroMemory(target);

        return value;
    }

}
