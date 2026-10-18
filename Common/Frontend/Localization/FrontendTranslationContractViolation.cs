namespace PasswordManagerLocal.Common.Frontend.Localization;

public sealed record FrontendTranslationContractViolation(
    FrontendTranslationContractViolationKind Kind,
    string? Key);
