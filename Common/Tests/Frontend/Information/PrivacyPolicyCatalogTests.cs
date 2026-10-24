using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Information;

namespace PasswordManagerLocal.Common.Tests.Frontend.Information;

[TestClass]
public sealed class PrivacyPolicyCatalogTests
{
    [TestMethod]
    public void BothBundledLanguagesCoverTheAuditedDataFlowsAndDiscloseDraftStatus()
    {
        var catalog = PrivacyPolicyCatalog.Load();
        foreach (var language in new[] { AppLanguage.English, AppLanguage.Hungarian })
        {
            var document = catalog.ForLanguage(language);
            Assert.IsTrue(document.IsDraft, "Do not silently present an unreviewed policy as final.");
            Assert.IsNull(document.EffectiveOn, "The draft review date is not a legal effective date.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(document.StatusText));
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
