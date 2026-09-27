using PasswordManagerLocal.Common.Contracts.Preferences;

namespace PasswordManagerLocal.Windows.Agent.Localization;

public interface IAgentLocalizationResourceLoader
{
    Stream Open(AppLanguage language);
}
