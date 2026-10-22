using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Information;

namespace PasswordManagerLocal.Common.Tests.Frontend.Information;

[TestClass]
public sealed class HelpCatalogTests
{
    [TestMethod]
    public void EveryStableDeepLinkHasRealContentInBothLanguages()
    {
        // Load also enforces cross-language section, platform, category, and link contracts.
        var catalog = HelpCatalogLoader.Load();
        var ids = typeof(HelpTopicIds).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral).Select(field => (string)field.GetRawConstantValue()!).ToArray();

        foreach (var language in new[] { AppLanguage.English, AppLanguage.Hungarian })
        {
            var content = catalog.ForLanguage(language);
            Assert.AreEqual(4, content.Categories.Count);
            foreach (var id in ids)
            {
                Assert.IsTrue(content.Topics.TryGetValue(id, out var topic), $"{language}: {id}");
                Assert.IsFalse(string.IsNullOrWhiteSpace(topic!.Title), id);
                Assert.IsTrue(topic.Sections.Count >= 3, id);
                Assert.IsTrue(topic.Keywords.Count > 0, id);
                Assert.IsTrue(topic.RelatedTopicIds.All(content.Topics.ContainsKey), id);
            }
            Assert.IsTrue(content.Topics[HelpTopicIds.AddDevice].Sections.Any(section => section.Platform == HelpPlatform.Android));
            Assert.IsTrue(content.Topics[HelpTopicIds.AddDevice].Sections.Any(section => section.Platform == HelpPlatform.Windows));
            foreach (var id in new[] { HelpTopicIds.Disconnect, HelpTopicIds.DeleteAccount, HelpTopicIds.TransferBetweenProfiles })
                Assert.IsTrue(content.Topics[id].Sections.Any(section => section.Kind == HelpSectionKind.Warning), id);
        }
    }

    [TestMethod]
    public void UnsupportedLanguageUsesBundledEnglishContent()
    {
        var catalog = HelpCatalogLoader.Load();
        Assert.AreSame(catalog.ForLanguage(AppLanguage.English), catalog.ForLanguage((AppLanguage)int.MaxValue));
    }

    [TestMethod]
    public void RelatedTopicHistoryReturnsToTheExactPreviousRoute()
    {
        var history = new InformationNavigationHistory();
        var settings = new InformationRoute(InformationDestination.Settings);
        var help = new InformationRoute(InformationDestination.Help);
        var category = new InformationRoute(InformationDestination.HelpCategory, "user-guide");
        var first = new InformationRoute(InformationDestination.HelpTopic, HelpTopicIds.Disconnect);
        var related = new InformationRoute(InformationDestination.HelpTopic, HelpTopicIds.Sync);
        history.Reset(settings);
        foreach (var route in new[] { help, category, first, related })
            history.Navigate(route);
        history.Navigate(related); // Re-selecting the current destination must not add a Back step.

        foreach (var expected in new[] { first, category, help, settings })
        {
            Assert.IsTrue(history.TryGoBack(out var actual));
            Assert.AreEqual(expected, actual);
        }
        Assert.IsFalse(history.TryGoBack(out _));

        history.Reset(help);
        Assert.AreEqual(help, history.Current);
        Assert.IsFalse(history.TryGoBack(out _));
    }
}
