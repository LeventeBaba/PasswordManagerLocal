namespace PasswordManagerLocal.Common.Frontend.Localization;

internal readonly record struct FrontendLocalizationPreparation(
    FrontendLocalizationChangeStatus Status,
    FrontendLocalizationCandidate? Candidate);
