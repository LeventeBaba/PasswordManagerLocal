namespace PasswordManagerLocal.Common.Frontend.Security;

public sealed record PasswordStrengthResult(int Score, double EstimatedEntropyBits);
