namespace PasswordManagerLocal.Common.Backend.Models;

public enum RevocationReceiptKind : byte
{
    DurablyStored = 1,
    Applied = 2,
    AppliedReceiptStored = 3
}
