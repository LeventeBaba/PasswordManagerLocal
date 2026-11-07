using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Models;

public sealed class Group : IntegrityCheckableBase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public byte[] EncryptedPayload { get; set; } = [];
    public DateTimeOffset LastModifiedAt { get; set; } = DateTimeOffset.UtcNow;


    public ICollection<User> Users { get; set; } = [];





    internal void SetMember(User user, bool isMember)
    {
        var currentUser = Users.FirstOrDefault(member => member.UId == user.UId);
        var currentGroup = user.Groups.FirstOrDefault(group => group.Id == Id);
        if (isMember)
        {
            if (currentUser is null)
                Users.Add(user);
            if (currentGroup is null)
                user.Groups.Add(this);
        }
        else
        {
            if (currentUser is not null)
                Users.Remove(currentUser);
            if (currentGroup is not null)
                user.Groups.Remove(currentGroup);
        }
    }

    public override byte[] CalculateIntegrityHash() =>
        Hashing.SHA256Hash(hash =>
        {
            hash.Write(Id);
            hash.WriteBytes(EncryptedPayload);
            hash.Write(LastModifiedAt);
        });

}
