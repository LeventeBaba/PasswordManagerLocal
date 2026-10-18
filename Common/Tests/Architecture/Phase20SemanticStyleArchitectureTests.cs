using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Frontend.Theming;

namespace PasswordManagerLocal.Common.Tests.Architecture;

[TestClass]
public sealed class Phase20SemanticStyleArchitectureTests
{
    private static readonly HashSet<string> ColorPropertyNames = new(StringComparer.Ordinal)
    {
        "Background",
        "Foreground",
        "BorderBrush",
        "Fill",
        "Stroke",
        "Color",
        "CaretBrush",
        "SelectionBrush"
    };

    [TestMethod]
    public void DarkAndLightThemeDictionariesContainTheCompleteSemanticContract()
    {
        var root = GetRepositoryRoot();
        var expectedKeys = ThemeContract.RequiredResources
            .Select(ThemeContract.GetResourceName)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        var xamlKey = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Key";

        foreach (var theme in new[] { "Dark", "Light" })
        {
            var path = Path.Combine(root, "Common", "Frontend", "Assets", "Themes", theme, "Theme.axaml");
            var dictionary = XDocument.Load(path);
            var resourceKeys = dictionary.Root!
                .Elements()
                .Select(element => (string?)element.Attribute(xamlKey))
                .Where(key => key is not null)
                .Cast<string>()
                .ToArray();

            Assert.AreEqual(resourceKeys.Length, resourceKeys.Distinct(StringComparer.Ordinal).Count(),
                $"{theme} theme contains duplicate resource keys.");
            CollectionAssert.AreEquivalent(expectedKeys, resourceKeys, $"{theme} theme does not match ThemeContract.");
        }
    }

    [TestMethod]
    public void SharedCardCornerRadiusMetricUsesTheCornerRadiusType()
    {
        var frontend = Path.Combine(GetRepositoryRoot(), "Common", "Frontend");
        var resourcePath = Path.Combine(frontend, "Assets", "Styles", "SharedUiResources.axaml");
        var stylePath = Path.Combine(frontend, "Assets", "Styles", "CommonStyles.axaml");
        var xamlKey = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Key";
        const string metricKey = "AppMetric.CornerRadius.Card";

        var resources = XDocument.Load(resourcePath);
        var metric = resources.Root!
            .Elements()
            .SingleOrDefault(element => (string?)element.Attribute(xamlKey) == metricKey);

        Assert.IsNotNull(metric, "The shared card-corner metric is missing.");
        Assert.AreEqual("CornerRadius", metric.Name.LocalName,
            "A CornerRadius setter requires a CornerRadius resource, not a numeric value.");
        Assert.AreEqual("18", metric.Value.Trim());

        var styles = XDocument.Load(stylePath);
        Assert.IsTrue(styles.Descendants()
            .Any(element => element.Name.LocalName == "Setter" &&
                            (string?)element.Attribute("Property") == "CornerRadius" &&
                            (string?)element.Attribute("Value") == "{StaticResource AppMetric.CornerRadius.Card}"),
            "The shared card style should consume the typed corner-radius metric.");
    }

    [TestMethod]
    public void ApplicationAndPageXamlKeepColorLiteralsInApprovedResourcesOnly()
    {
        var frontend = Path.Combine(GetRepositoryRoot(), "Common", "Frontend");
        var approvedResourceFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(Path.Combine(frontend, "Assets", "Styles", "SharedUiResources.axaml"))
        };
        var violations = new List<string>();

        foreach (var path in Directory.EnumerateFiles(frontend, "*.axaml", SearchOption.AllDirectories))
        {
            var fullPath = Path.GetFullPath(path);
            var relativePath = Path.GetRelativePath(frontend, fullPath).Replace('\\', '/');
            if (relativePath.StartsWith("Assets/Themes/", StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith("Assets/Icons/", StringComparison.OrdinalIgnoreCase) ||
                approvedResourceFiles.Contains(fullPath))
            {
                continue;
            }

            var document = XDocument.Load(fullPath);
            foreach (var element in document.Descendants())
            {
                foreach (var attribute in element.Attributes())
                {
                    if (ColorPropertyNames.Contains(attribute.Name.LocalName) &&
                        IsColorLiteral(attribute.Value) && !IsApprovedTransparentLiteral(attribute.Value))
                    {
                        violations.Add($"{relativePath}: {attribute.Name.LocalName}=\"{attribute.Value}\"");
                    }
                }

                if (element.Name.LocalName == "Setter" &&
                    ColorPropertyNames.Contains((string?)element.Attribute("Property") ?? string.Empty) &&
                    IsColorLiteral((string?)element.Attribute("Value") ?? string.Empty) &&
                    !IsApprovedTransparentLiteral((string?)element.Attribute("Value") ?? string.Empty))
                {
                    violations.Add($"{relativePath}: Setter {element.Attribute("Property")?.Value}=\"{element.Attribute("Value")?.Value}\"");
                }
            }
        }

        Assert.AreEqual(0, violations.Count, string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void ViewModelsExposeUserColorValuesButNoPresentationBrushProperties()
    {
        var viewModelDirectory = Path.Combine(GetRepositoryRoot(), "Common", "Frontend", "ViewModels");
        var sources = string.Join('\n', Directory.EnumerateFiles(viewModelDirectory, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText));

        Assert.IsFalse(Regex.IsMatch(sources, @"\b(?:IBrush|SolidColorBrush)\??\s+[A-Za-z_]\w*\s*(?:[;={])"),
            "View models should expose semantic state and raw user color values, not Avalonia brushes.");
        Assert.IsFalse(sources.Contains("Brush.Parse", StringComparison.Ordinal));
        Assert.IsFalse(sources.Contains("Brushes.", StringComparison.Ordinal));

        var colorProperties = Regex.Matches(
                sources,
                @"\b(?:public|protected|internal)\s+(?:Avalonia\.Media\.)?Color\s+([A-Za-z_]\w*)\s*(?:\{|=>)")
            .Cast<Match>()
            .Select(match => match.Groups[1].Value)
            .ToArray();
        CollectionAssert.AreEqual(
            new[] { "CustomColorPickerColor" },
            colorProperties,
            "The ColorPicker's user-entered color value is the sole Avalonia Color property exception.");
    }

    private static bool IsColorLiteral(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('{'))
        {
            return Regex.IsMatch(trimmed, @"(?i)(?:\bColors?\.[A-Za-z_]\w*|#[0-9A-F]{3,8}\b|\b(?:rgb|rgba|hsl|hsla|sc)\s*[\(\[])");
        }

        try
        {
            _ = Color.Parse(trimmed);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // Transparent-only control surfaces preserve existing interaction templates and carry no palette color.
    private static bool IsApprovedTransparentLiteral(string value) =>
        string.Equals(value.Trim(), "Transparent", StringComparison.OrdinalIgnoreCase);

    private static string GetRepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "..", ".."));
}
