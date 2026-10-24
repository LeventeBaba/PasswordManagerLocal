using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Information;

namespace PasswordManagerLocal.Common.Tests.Frontend.Information;

[TestClass]
public sealed class HelpSearchTests
{
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    public void SearchRequiresEveryWordAndRanksTitlesAheadOfBodyMatches()
    {
        var index = Index(
            Topic("body", "Device care", "General guidance", [], "Read about backup recovery offline."),
            Topic("title", "Backup recovery", "Plan offline access", ["restore"], "Keep access to another device."));

        var matches = index.Search("backup recovery");

        Assert.AreEqual(2, matches.Count);
        Assert.AreEqual("title", matches[0].Topic.Id);
        Assert.AreEqual("title", index.Search("restore offline").Single().Topic.Id);
        Assert.AreEqual(0, index.Search("restore nonexistent").Count);
        Assert.AreEqual(0, index.Search("  ").Count);
        Assert.AreEqual(0, index.Search("---").Count);
    }

    [TestMethod]
    public void SearchIgnoresCaseAccentsAndWordSeparators()
    {
        var index = Index(Topic("hu", "Őrizd a jelszót", "Biztonsági útmutató", ["emlékezz rám"], "Védd az eszközödet."));

        Assert.AreEqual("hu", index.Search("ORIZD JELSZOT").Single().Topic.Id);
        Assert.AreEqual("hu", index.Search("emlekezz-ram").Single().Topic.Id);

        var catalog = HelpCatalogLoader.Load();
        var accented = catalog.Search(AppLanguage.Hungarian, HelpPlatform.Windows, "JELSZÓ");
        var unaccented = catalog.Search(AppLanguage.Hungarian, HelpPlatform.Windows, "jelszo");
        Assert.IsTrue(accented.Count > 0);
        CollectionAssert.AreEqual(accented.Select(item => item.Topic.Id).ToArray(),
            unaccented.Select(item => item.Topic.Id).ToArray());
    }

    [TestMethod]
    public void SearchAndExcerptsOnlyUseSectionsVisibleOnTheCurrentPlatform()
    {
        var topic = new HelpTopic("platform", "guides", "Connect devices", "Connection guidance", [],
            [new("shared", HelpSectionKind.Explanation, HelpPlatform.All, "Network", "sharednetwork"),
             new("phone", HelpSectionKind.Step, HelpPlatform.Android, "Phone", "camerapermission"),
             new("desktop", HelpSectionKind.Step, HelpPlatform.Windows, "Desktop", "desktopfirewall")], []);
        var content = Content(topic);
        var windows = new HelpSearchIndex(content, HelpPlatform.Windows);
        var android = new HelpSearchIndex(content, HelpPlatform.Android);

        Assert.AreEqual(0, windows.Search("camerapermission").Count);
        Assert.AreEqual(0, android.Search("desktopfirewall").Count);
        Assert.AreEqual("camerapermission", android.Search("camerapermission").Single().Excerpt);
        Assert.AreEqual("desktopfirewall", windows.Search("desktopfirewall").Single().Excerpt);
        Assert.AreEqual(1, windows.Search("sharednetwork").Count);
        Assert.AreEqual(1, android.Search("sharednetwork").Count);
    }

    [TestMethod]
    public void BodyMatchesShowAnExcerptAroundTheMatchInsteadOfTheStartOfTheArticle()
    {
        var body = string.Concat(Enumerable.Repeat("Introduction text. ", 50)) +
            "Find the connectionneedle in this part. " + string.Concat(Enumerable.Repeat("More guidance. ", 30));
        var result = Index(Topic("long", "Device connection", "General introduction", [], body))
            .Search("connectionneedle").Single();

        StringAssert.Contains(result.Excerpt, "connectionneedle");
        Assert.IsTrue(result.Excerpt.StartsWith("…", StringComparison.Ordinal));
        Assert.IsTrue(result.Excerpt.Length <= 222);
        Assert.AreEqual("Guides", result.CategoryTitle);
    }

    [TestMethod]
    public void LongUnbrokenWordsStillProduceANonemptyExcerptContainingTheMatch()
    {
        foreach (var body in new[]
                 {
                     "connectionneedle" + new string('x', 800),
                     new string('x', 500) + "connectionneedle" + new string('x', 500)
                 })
        {
            var result = Index(Topic("long-token", "Device connection", "General guidance", [], body))
                .Search("connectionneedle").Single();
            StringAssert.Contains(result.Excerpt, "connectionneedle");
            Assert.IsTrue(result.Excerpt.Length <= 222);
        }
    }

    [TestMethod]
    public void FullCatalogSearchFindsRealTermsOnBothPlatformsAndReportsRuntimeTiming()
    {
        var catalog = HelpCatalogLoader.Load();
        foreach (var language in new[] { AppLanguage.English, AppLanguage.Hungarian })
        foreach (var platform in new[] { HelpPlatform.Windows, HelpPlatform.Android })
        {
            var cases = language == AppLanguage.English
                ? new[]
                {
                    (Query: "blocked device", Id: HelpTopicIds.Blocked, First: true),
                    (Query: "QR", Id: HelpTopicIds.EnrollmentOverview, First: false),
                    (Query: "switch profile", Id: HelpTopicIds.SwitchProfiles, First: true),
                    (Query: "disconnect", Id: HelpTopicIds.Disconnect, First: true),
                    (Query: "remember me", Id: HelpTopicIds.RememberMe, First: false)
                }
                : new[]
                {
                    (Query: "tiltott eszkoz", Id: HelpTopicIds.Blocked, First: true),
                    (Query: "QR", Id: HelpTopicIds.EnrollmentOverview, First: false),
                    (Query: "profilvaltas", Id: HelpTopicIds.SwitchProfiles, First: true),
                    (Query: "ESZKÖZ LEVÁLASZTÁSA", Id: HelpTopicIds.Disconnect, First: true),
                    (Query: "emlekezz ram", Id: HelpTopicIds.RememberMe, First: false)
                };
            var cold = Stopwatch.StartNew();
            catalog.Search(language, platform, cases[0].Query);
            cold.Stop();
            foreach (var item in cases)
            {
                var matches = catalog.Search(language, platform, item.Query);
                Assert.IsTrue(matches.Any(match => match.Topic.Id == item.Id), $"{language}/{platform}: {item.Query}");
                if (item.First)
                    Assert.AreEqual(item.Id, matches[0].Topic.Id, $"{language}/{platform}: {item.Query}");
            }
            if (language == AppLanguage.Hungarian)
                CollectionAssert.AreEqual(
                    catalog.Search(language, platform, "ESZKÖZ LEVÁLASZTÁSA").Select(match => match.Topic.Id).ToArray(),
                    catalog.Search(language, platform, "eszkoz levalasztasa").Select(match => match.Topic.Id).ToArray());

            const int repetitions = 128;
            var warm = Stopwatch.StartNew();
            for (var i = 0; i < repetitions; i++)
                catalog.Search(language, platform, cases[i % cases.Length].Query);
            warm.Stop();
            // Diagnostic output only: a hardware-dependent pass/fail timing limit would be flaky.
            TestContext.WriteLine($"Help search {language}/{platform}: {catalog.ForLanguage(language).Topics.Count} topics, " +
                $"index + first query {cold.Elapsed.TotalMilliseconds:F3} ms, " +
                $"warm query average {warm.Elapsed.TotalMilliseconds / repetitions:F3} ms.");
        }
    }

    private static HelpSearchIndex Index(params HelpTopic[] topics) => new(Content(topics), HelpPlatform.Windows);

    private static HelpCatalogContent Content(params HelpTopic[] topics) =>
        new([new HelpCategory("guides", "Guides", "Practical help")], topics.ToDictionary(topic => topic.Id));

    private static HelpTopic Topic(string id, string title, string summary, string[] keywords, string body) =>
        new(id, "guides", title, summary, keywords,
            [new HelpSection("body", HelpSectionKind.Explanation, HelpPlatform.All, "Details", body)], []);
}
