using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Models;

public sealed class Group : IntegrityCheckableBase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public byte[] EncryptedPayload { get; set; } = [];
    public DateTimeOffset LastModifiedAt { get; set; } = DateTimeOffset.UtcNow;


    public ICollection<User> Users { get; set; } = [];





    public override byte[] CalculateIntegrityHash() =>
        Hashing.SHA256Hash(hash =>
        {
            hash.Write(Id);
            hash.WriteBytes(EncryptedPayload);
            hash.Write(LastModifiedAt);
        });

}
