using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Frontend.Views.Styling;

namespace PasswordManagerLocal.Common.Tests.Frontend.Views;

[TestClass]
public sealed class NonNullDataTemplateTests
{
    [TestMethod]
    public void ExplicitTemplateBuildsOnlyForActiveContentAndForwardsCurrentData()
    {
        var inner = new RecordingTemplate();
        var template = new NonNullDataTemplate { Template = inner };

        Assert.IsTrue(template.Match(null));
        Assert.IsNull(template.Build(null));
        Assert.AreEqual(0, inner.BuildCount);

        var firstSession = new object();
        Assert.IsTrue(template.Match(firstSession));
        template.Build(firstSession);
        Assert.AreEqual(1, inner.BuildCount);
        Assert.AreSame(firstSession, inner.LastData);
        Assert.IsNull(template.Build(null));
        Assert.AreEqual(1, inner.BuildCount);

        var nextSession = new object();
        template.Build(nextSession);
        Assert.AreEqual(2, inner.BuildCount);
        Assert.AreSame(nextSession, inner.LastData);
    }

    private sealed class RecordingTemplate : IDataTemplate
    {
        public int BuildCount { get; private set; }
        public object? LastData { get; private set; }
        public bool Match(object? data) => true;
        public Control? Build(object? data)
        {
            BuildCount++;
            LastData = data;
            return null;
        }
    }
}
