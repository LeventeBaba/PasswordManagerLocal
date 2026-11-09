using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Constants;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Information;

namespace PasswordManagerLocal.Common.Tests.Frontend.Information;

[TestClass]
public sealed class PrivacyPolicyCatalogTests
{
    [TestMethod]
    public void BothBundledLanguagesContainTheEffectivePolicy()
    {
        var catalog = PrivacyPolicyCatalog.Load();
        foreach (var language in new[] { AppLanguage.English, AppLanguage.Hungarian })
        {
            var document = catalog.ForLanguage(language);
            Assert.IsFalse(document.IsDraft);
            Assert.IsNotNull(document.EffectiveOn);
            StringAssert.Contains(document.StatusText, ApplicationBranding.DeveloperEmail);
            StringAssert.Contains(document.Sections.Single(section => section.IsRightsSection).Body,
                ApplicationBranding.DeveloperEmail);
            foreach (var id in new[]
                     {
                         "scope-contact", "profile-data", "vault-data", "device-data", "local-security",
                         "peer-network", "background-permissions", "qr-files", "clipboard",
                         "retention-deletion", "diagnostics", "backups", "external-services",
                         "legal-basis", "rights", "automation-children", "changes"
                     })
                Assert.IsTrue(document.Sections.Any(section => section.Id == id &&
                    !string.IsNullOrWhiteSpace(section.Heading) && !string.IsNullOrWhiteSpace(section.Body)), id);
        }
        Assert.AreSame(catalog.ForLanguage(AppLanguage.English), catalog.ForLanguage((AppLanguage)int.MaxValue));
    }
}
