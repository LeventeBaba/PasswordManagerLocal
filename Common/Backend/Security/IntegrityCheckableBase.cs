using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Exceptions;

namespace PasswordManagerLocal.Common.Backend.Security;

public abstract class IntegrityCheckableBase : IIntegrityCheckable
{
    public byte[] IntegrityHash { get; set; } = [];

    public abstract byte[] CalculateIntegrityHash();

    public virtual bool IsIntegrityValid()
    {
        if (IntegrityHash.Length != CryptographyConstants.Sha256HashSizeInBytes)
            return false;

        var calculatedHash = CalculateIntegrityHash();
        return calculatedHash.Length == CryptographyConstants.Sha256HashSizeInBytes &&
               Hashing.Verify(IntegrityHash, calculatedHash);
    }

    public virtual void GenerateIntegrityHash()
    {
        var calculatedHash = CalculateIntegrityHash();
        if (calculatedHash.Length != CryptographyConstants.Sha256HashSizeInBytes)
            throw new InvalidOperationException($"Integrity hashes must be SHA-256 hashes ({CryptographyConstants.Sha256HashSizeInBytes} bytes).");

        IntegrityHash = calculatedHash;
    }

    public virtual void VerifyIntegrity()
    {
        if (!IsIntegrityValid())
            throw new InvalidDataIntegrityException(this.GetType());
    }
}