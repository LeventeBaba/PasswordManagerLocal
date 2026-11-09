using System.Collections.Frozen;
using PasswordManagerLocal.Common.Contracts.Preferences;

namespace PasswordManagerLocal.Common.Frontend.Localization;

internal sealed record FrontendTranslationSnapshot(
    AppLanguage Language,
    FrozenDictionary<string, string> Translations);
