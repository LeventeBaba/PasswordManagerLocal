using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PasswordManagerLocal.Common.Tests.Architecture;

[TestClass]
public sealed class Phase21ControlThemeArchitectureTests
{
    [TestMethod]
    public void ButtonUsesAnApplicationOwnedSemanticControlTheme()
    {
        var root = GetRepositoryRoot();
        var themesPath = Path.Combine(root, "Common", "Frontend", "Assets", "Styles", "ControlThemes.axaml");
        var themes = XDocument.Load(themesPath);
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var buttonTheme = themes.Root!.Elements(avalonia + "ControlTheme")
            .Single(theme => (string?)theme.Attribute(xaml + "Key") == "{x:Type Button}");

        Assert.AreEqual("Button", (string?)buttonTheme.Attribute("TargetType"));
        var template = buttonTheme.Descendants(avalonia + "ControlTemplate").Single();
        var templateText = template.ToString(SaveOptions.DisableFormatting);
        foreach (var requiredBinding in new[]
                 {
                     "{TemplateBinding Content}",
                     "{TemplateBinding ContentTemplate}",
                     "{TemplateBinding Padding}",
                     "{TemplateBinding HorizontalContentAlignment}",
                     "{TemplateBinding VerticalContentAlignment}",
                     "RecognizesAccessKey=\"True\"",
                     "PART_FocusRing"
                 })
        {
            StringAssert.Contains(templateText, requiredBinding);
        }

        var themeText = buttonTheme.ToString(SaveOptions.DisableFormatting);
        foreach (var stateResource in new[]
                 {
                     "AppBrush.Surface.Subtle",
                     "AppBrush.Text.Primary",
                     "AppBrush.Border.Subtle",
                     "AppBrush.Focus"
                 })
        {
            StringAssert.Contains(themeText, stateResource);
        }

        foreach (var themeName in new[] { "Dark", "Light" })
        {
            var themeDictionary = XDocument.Load(Path.Combine(
                root, "Common", "Frontend", "Assets", "Themes", themeName, "Theme.axaml"));
            var resourceKeys = themeDictionary.Descendants()
                .Attributes(xaml + "Key")
                .Select(attribute => attribute.Value)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var stateResource in new[]
                     {
                         "AppBrush.Surface.Subtle",
                         "AppBrush.Text.Primary",
                         "AppBrush.Border.Subtle",
                         "AppBrush.Focus",
                         "AppBrush.Action.DisabledForeground",
                         "AppBrush.Action.Secondary.DisabledSurface",
                         "AppBrush.Action.Primary.DisabledSurface",
                         "AppBrush.Action.Danger.DisabledSurface"
                     })
            {
                Assert.IsTrue(resourceKeys.Contains(stateResource),
                    $"{themeName} is missing a Button state resource {stateResource}.");
            }
        }

        foreach (var stateSelector in new[] { ":pointerover", ":pressed", ":focus-visible" })
            StringAssert.Contains(themeText, stateSelector);

        var commonStylesPath = Path.Combine(
            root, "Common", "Frontend", "Assets", "Styles", "CommonStyles.axaml");
        var commonStyles = XDocument.Load(commonStylesPath);
        var disabledButtonStyles = commonStyles.Descendants(avalonia + "Style")
            .Where(style => ((string?)style.Attribute("Selector"))?.Contains("Button") == true
                            && ((string?)style.Attribute("Selector"))?.Contains(":disabled") == true)
            .ToDictionary(style => (string)style.Attribute("Selector")!, StringComparer.Ordinal);

        AssertDisabledButtonStyle(disabledButtonStyles, "Button:disabled", new Dictionary<string, string>
        {
            ["Background"] = "{DynamicResource AppBrush.Action.Secondary.DisabledSurface}",
            ["Foreground"] = "{DynamicResource AppBrush.Action.DisabledForeground}",
            ["BorderBrush"] = "{DynamicResource AppBrush.Border.SubtleStrong}"
        });
        AssertDisabledButtonStyle(disabledButtonStyles, "Button.primary:disabled", new Dictionary<string, string>
        {
            ["Background"] = "{DynamicResource AppBrush.Action.Primary.DisabledSurface}",
            ["Foreground"] = "{DynamicResource AppBrush.Action.DisabledForeground}",
            ["BorderBrush"] = "{DynamicResource AppBrush.Action.Primary.DisabledSurface}"
        });
        AssertDisabledButtonStyle(disabledButtonStyles, "Button.danger:disabled", new Dictionary<string, string>
        {
            ["Background"] = "{DynamicResource AppBrush.Action.Danger.DisabledSurface}",
            ["Foreground"] = "{DynamicResource AppBrush.Action.DisabledForeground}",
            ["BorderBrush"] = "{DynamicResource AppBrush.Action.Danger.DisabledSurface}"
        });
    }

    private static void AssertDisabledButtonStyle(
        IReadOnlyDictionary<string, XElement> styles,
        string selector,
        IReadOnlyDictionary<string, string> expectedSetters)
    {
        Assert.IsTrue(styles.TryGetValue(selector, out var style),
            $"CommonStyles.axaml is missing the disabled Button style {selector}.");
        var setters = style!.Elements().Where(element => element.Name.LocalName == "Setter")
            .ToDictionary(
                setter => (string)setter.Attribute("Property")!,
                setter => (string)setter.Attribute("Value")!,
                StringComparer.Ordinal);
        foreach (var (property, value) in expectedSetters)
        {
            Assert.AreEqual(value, setters.GetValueOrDefault(property),
                $"Disabled Button style {selector} must use its semantic {property} resource.");
        }
        Assert.IsFalse(setters.ContainsKey("Opacity"),
            $"Disabled Button style {selector} must preserve label contrast without opacity fading.");
    }

    [TestMethod]
    public void CheckBoxUsesAnApplicationOwnedSemanticThemeWithSelectionAndFocusStates()
    {
        var root = GetRepositoryRoot();
        var themesPath = Path.Combine(root, "Common", "Frontend", "Assets", "Styles", "ControlThemes.axaml");
        var themes = XDocument.Load(themesPath);
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var checkBoxTheme = themes.Root!.Elements(avalonia + "ControlTheme")
            .Single(theme => (string?)theme.Attribute(xaml + "Key") == "{x:Type CheckBox}");

        Assert.AreEqual("CheckBox", (string?)checkBoxTheme.Attribute("TargetType"));
        var template = checkBoxTheme.Descendants(avalonia + "ControlTemplate").Single();
        var templateText = template.ToString(SaveOptions.DisableFormatting);
        foreach (var requiredPart in new[]
                 {
                     "TemplatedControl.IsTemplateFocusTarget=\"True\"",
                     "{TemplateBinding Content}",
                     "{TemplateBinding ContentTemplate}",
                     "{TemplateBinding Padding}",
                     "{TemplateBinding Foreground}",
                     "{TemplateBinding HorizontalContentAlignment}",
                     "{TemplateBinding VerticalContentAlignment}",
                     "PART_CheckBoxBorder",
                     "PART_CheckMark",
                     "PART_IndeterminateMark",
                     "PART_FocusRing",
                     "RecognizesAccessKey=\"True\""
                 })
        {
            StringAssert.Contains(templateText, requiredPart);
        }

        var themeText = checkBoxTheme.ToString(SaveOptions.DisableFormatting);
        foreach (var stateSelector in new[]
                 {
                     ":pointerover",
                     ":pressed",
                     ":checked",
                     ":indeterminate",
                     ":disabled",
                     ":focus"
                 })
        {
            StringAssert.Contains(themeText, stateSelector);
        }

        var requiredResources = new[]
        {
            "AppBrush.Action.Primary",
            "AppBrush.Action.PrimaryForeground",
            "AppBrush.Border.Emphasis",
            "AppBrush.Border.Subtle",
            "AppBrush.Border.SubtleStrong",
            "AppBrush.Focus",
            "AppBrush.Surface.Pressed",
            "AppBrush.Surface.SubtleEmphasis",
            "AppBrush.Text.Disabled",
            "AppBrush.Text.Primary"
        };
        foreach (var resource in requiredResources)
        {
            StringAssert.Contains(themeText, resource);
        }

        foreach (var themeName in new[] { "Dark", "Light" })
        {
            var themeDictionary = XDocument.Load(Path.Combine(
                root, "Common", "Frontend", "Assets", "Themes", themeName, "Theme.axaml"));
            var resourceKeys = themeDictionary.Descendants()
                .Attributes(xaml + "Key")
                .Select(attribute => attribute.Value)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var resource in requiredResources)
            {
                Assert.IsTrue(resourceKeys.Contains(resource),
                    $"{themeName} is missing the CheckBox theme resource {resource}.");
            }
        }

        var viewFiles = Directory.GetFiles(
            Path.Combine(root, "Common", "Frontend", "Views"), "*.axaml", SearchOption.AllDirectories);
        Assert.IsTrue(viewFiles.Any(path => XDocument.Load(path).Descendants(avalonia + "CheckBox").Any()),
            "The source audit expects the app's CheckBox control to remain represented in the UI.");
    }

    [TestMethod]
    public void ToggleSwitchUsesAnApplicationOwnedThemeWithRequiredPartsAndTouchStates()
    {
        var root = GetRepositoryRoot();
        var themesPath = Path.Combine(root, "Common", "Frontend", "Assets", "Styles", "ControlThemes.axaml");
        var themes = XDocument.Load(themesPath);
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var switchTheme = themes.Root!.Elements(avalonia + "ControlTheme")
            .Single(theme => (string?)theme.Attribute(xaml + "Key") == "{x:Type ToggleSwitch}");

        Assert.AreEqual("ToggleSwitch", (string?)switchTheme.Attribute("TargetType"));
        var template = switchTheme.Descendants(avalonia + "ControlTemplate").Single();
        var templateText = template.ToString(SaveOptions.DisableFormatting);
        foreach (var requiredPart in new[]
                 {
                     "TemplatedControl.IsTemplateFocusTarget=\"True\"",
                     "{TemplateBinding Content}",
                     "{TemplateBinding ContentTemplate}",
                     "{TemplateBinding OffContent}",
                     "{TemplateBinding OffContentTemplate}",
                     "{TemplateBinding OnContent}",
                     "{TemplateBinding OnContentTemplate}",
                     "PART_ContentPresenter",
                     "PART_OffContentPresenter",
                     "PART_OnContentPresenter",
                     "PART_SwitchKnob",
                     "PART_MovingKnobs"
                 })
        {
            StringAssert.Contains(templateText, requiredPart);
        }
        Assert.IsTrue(HasSetter(switchTheme, avalonia, "FocusAdorner", "{x:Null}"));

        var offContentPresenter = template.Descendants(avalonia + "ContentPresenter")
            .Single(presenter => (string?)presenter.Attribute(xaml + "Name") == "PART_OffContentPresenter");
        var onContentPresenter = template.Descendants(avalonia + "ContentPresenter")
            .Single(presenter => (string?)presenter.Attribute(xaml + "Name") == "PART_OnContentPresenter");
        Assert.AreEqual("0,0,2,0", (string?)offContentPresenter.Attribute("Margin"));
        Assert.AreEqual("0,0,2,0", (string?)onContentPresenter.Attribute("Margin"));
        Assert.IsFalse(template.Descendants().Any(element =>
            (string?)element.Attribute(xaml + "Name") == "PART_FocusRing" ||
            (string?)element.Attribute("Name") == "PART_FocusRing"));

        var focusVisibleStyle = switchTheme.Elements(avalonia + "Style")
            .Single(style => (string?)style.Attribute("Selector") ==
                "^:focus-visible /template/ Border#OuterBorder");
        Assert.IsTrue(HasSetter(focusVisibleStyle, avalonia,
            "BorderBrush", "{DynamicResource AppBrush.Focus}"));
        Assert.IsTrue(HasSetter(focusVisibleStyle, avalonia, "BorderThickness", "2"));

        var themeText = switchTheme.ToString(SaveOptions.DisableFormatting);
        foreach (var stateSelector in new[]
                 {
                     ":pointerover",
                     ":pressed",
                     ":dragging",
                     ":checked",
                     ":unchecked",
                     ":disabled",
                     ":focus-visible"
                 })
        {
            StringAssert.Contains(themeText, stateSelector);
        }

        var requiredResources = new[]
        {
            "AppBrush.Action.Primary",
            "AppBrush.Action.PrimaryForeground",
            "AppBrush.Border.Emphasis",
            "AppBrush.Border.SubtleStrong",
            "AppBrush.Focus",
            "AppBrush.Surface.Pressed",
            "AppBrush.Surface.SubtleEmphasis",
            "AppBrush.Text.Disabled",
            "AppBrush.Text.Primary",
            "AppBrush.Text.Secondary"
        };
        foreach (var resource in requiredResources)
        {
            StringAssert.Contains(themeText, resource);
        }
        StringAssert.Contains(themeText, "AppMetric.TouchTarget.Standard");

        foreach (var themeName in new[] { "Dark", "Light" })
        {
            var themeDictionary = XDocument.Load(Path.Combine(
                root, "Common", "Frontend", "Assets", "Themes", themeName, "Theme.axaml"));
            var resourceKeys = themeDictionary.Descendants()
                .Attributes(xaml + "Key")
                .Select(attribute => attribute.Value)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var resource in requiredResources)
            {
                Assert.IsTrue(resourceKeys.Contains(resource),
                    $"{themeName} is missing the ToggleSwitch theme resource {resource}.");
            }
        }

        var viewFiles = Directory.GetFiles(
            Path.Combine(root, "Common", "Frontend", "Views"), "*.axaml", SearchOption.AllDirectories);
        Assert.IsTrue(viewFiles.Any(path => XDocument.Load(path).Descendants(avalonia + "ToggleSwitch").Any()),
            "The source audit expects the app's ToggleSwitch control to remain represented in the UI.");
    }

    [TestMethod]
    public void ApplicationLoadsItsApplicationOwnedControlThemes()
    {
        var root = GetRepositoryRoot();
        var app = XDocument.Load(Path.Combine(root, "Common", "Frontend", "App.axaml"));
        XNamespace avalonia = "https://github.com/avaloniaui";
        var includedDictionaries = app.Descendants(avalonia + "ResourceInclude")
            .Select(include => (string?)include.Attribute("Source"))
            .Where(source => source is not null)
            .ToHashSet(StringComparer.Ordinal);
        Assert.IsTrue(includedDictionaries.Contains(
            "avares://PasswordManagerLocal.Common.Frontend/Assets/Styles/ControlThemes.axaml"));
        Assert.IsTrue(includedDictionaries.Contains(
            "avares://PasswordManagerLocal.Common.Frontend/Assets/Styles/ColorPickerThemes.axaml"));
        var controlThemes = XDocument.Load(Path.Combine(
            root, "Common", "Frontend", "Assets", "Styles", "ControlThemes.axaml"));
        var colorPickerThemes = XDocument.Load(Path.Combine(
            root, "Common", "Frontend", "Assets", "Styles", "ColorPickerThemes.axaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var appOwnedDefaultThemeCount = new[] { controlThemes, colorPickerThemes }
            .Sum(dictionary => dictionary.Root!.Elements(avalonia + "ControlTheme")
                .Count(theme => ((string?)theme.Attribute(xaml + "Key"))
                    ?.StartsWith("{x:Type ", StringComparison.Ordinal) == true));
        Assert.AreEqual(27, appOwnedDefaultThemeCount,
            "Twenty-seven app-owned default control themes should cover the migrated control families and Android popup root.");
    }

    [TestMethod]
    public void ColorViewUsesAnApplicationOwnedTemplateAndPreservesTheColorEditingContract()
    {
        var root = GetRepositoryRoot();
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace primitives = "using:Avalonia.Controls.Primitives";
        var themes = XDocument.Load(Path.Combine(
            root, "Common", "Frontend", "Assets", "Styles", "ColorPickerThemes.axaml"));
        var theme = themes.Root!.Elements(avalonia + "ControlTheme")
            .Single(item => (string?)item.Attribute(xaml + "Key") == "{x:Type ColorView}");

        Assert.AreEqual("ColorView", (string?)theme.Attribute("TargetType"));
        var template = theme.Descendants(avalonia + "ControlTemplate").Single();
        var tabControl = template.Descendants(avalonia + "TabControl")
            .Single(control => (string?)control.Attribute(xaml + "Name") == "PART_TabControl");
        Assert.AreEqual(
            "{Binding SelectedIndex, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}",
            (string?)tabControl.Attribute("SelectedIndex"));

        Assert.IsTrue(template.Descendants(avalonia + "TextBox")
            .Any(control => (string?)control.Attribute(xaml + "Name") == "PART_HexTextBox"),
            "ColorView uses PART_HexTextBox to synchronize and validate hexadecimal colors.");
        Assert.IsTrue(template.Descendants(primitives + "ColorSpectrum").Any(),
            "ColorView must retain the editable color spectrum.");
        Assert.IsTrue(template.Descendants(primitives + "ColorSlider").Count() >= 5,
            "ColorView must retain the spectrum and component sliders.");
        Assert.IsTrue(template.Descendants(primitives + "ColorPreviewer").Any(),
            "ColorView must retain the current-color preview.");

        foreach (var property in new[]
                 {
                     "IsColorSpectrumVisible",
                     "IsColorPaletteVisible",
                     "IsColorComponentsVisible",
                     "IsColorModelVisible",
                     "IsAlphaEnabled",
                     "IsAlphaVisible",
                     "IsColorPreviewVisible",
                     "ColorSpectrumComponents",
                     "ColorSpectrumShape",
                     "HsvColor"
                 })
        {
            Assert.IsTrue(template.Descendants().Any(element =>
                    element.Attributes().Any(attribute => attribute.Value.Contains(
                        property, StringComparison.Ordinal))),
                $"The ColorView template must continue forwarding {property}.");
        }
    }

    [TestMethod]
    public void ColorPickerSlidersAndComponentEditorsUseApplicationOwnedThemes()
    {
        var root = GetRepositoryRoot();
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace primitives = "using:Avalonia.Controls.Primitives";
        var themes = XDocument.Load(Path.Combine(
            root, "Common", "Frontend", "Assets", "Styles", "ColorPickerThemes.axaml"));
        var dictionary = themes.Root!;
        var sliderTheme = dictionary.Elements(avalonia + "ControlTheme")
            .Single(item => (string?)item.Attribute(xaml + "Key") == "{x:Type ColorSlider}");
        Assert.AreEqual("ColorSlider", (string?)sliderTheme.Attribute("TargetType"));
        Assert.IsTrue(sliderTheme.Elements(avalonia + "Style")
            .Any(style => (string?)style.Attribute("Selector") == "^:horizontal"));
        Assert.IsTrue(sliderTheme.Elements(avalonia + "Style")
            .Any(style => (string?)style.Attribute("Selector") == "^:vertical"));
        var sliderTemplates = sliderTheme.Descendants(avalonia + "ControlTemplate")
            .Where(template => (string?)template.Attribute("TargetType") == "ColorSlider")
            .ToList();
        Assert.AreEqual(2, sliderTemplates.Count,
            "The app-owned ColorSlider theme needs horizontal and vertical templates.");
        foreach (var sliderTemplate in sliderTemplates)
        {
            var partNames = sliderTemplate.Descendants()
                .Select(element => (string?)element.Attribute("Name") ??
                                   (string?)element.Attribute(xaml + "Name"))
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
            foreach (var requiredPart in new[]
                     { "PART_Track", "PART_DecreaseButton", "PART_IncreaseButton", "ColorSliderThumb" })
            {
                Assert.IsTrue(partNames.Contains(requiredPart),
                    $"The ColorSlider template must retain {requiredPart}.");
            }
        }
        Assert.IsTrue(sliderTheme.Descendants(avalonia + "Thumb")
            .Any(thumb => (string?)thumb.Attribute("Theme") ==
                          "{DynamicResource AppColorSliderThumbTheme}"));

        var colorView = dictionary.Elements(avalonia + "ControlTheme")
            .Single(item => (string?)item.Attribute(xaml + "Key") == "{x:Type ColorView}");
        var paletteList = colorView.Descendants(avalonia + "ListBox").Single();
        Assert.AreEqual("{StaticResource ColorViewPaletteListBoxTheme}",
            (string?)paletteList.Attribute("Theme"));
        Assert.AreEqual("{StaticResource ColorViewPaletteListBoxItemTheme}",
            (string?)paletteList.Attribute("ItemContainerTheme"));
        var modelRadioButtons = colorView.Descendants(avalonia + "RadioButton").ToList();
        Assert.AreEqual(2, modelRadioButtons.Count,
            "ColorView must retain the RGB and HSV model selectors.");
        foreach (var radioButton in modelRadioButtons)
        {
            Assert.AreEqual("{StaticResource ColorViewColorModelRadioButtonTheme}",
                (string?)radioButton.Attribute("Theme"),
                "Color model selectors must not depend on a global theme fallback.");
        }
        foreach (var (themeKey, targetType) in new[]
                 {
                     ("ColorViewPaletteListBoxTheme", "ListBox"),
                     ("ColorViewPaletteListBoxItemTheme", "ListBoxItem"),
                     ("ColorViewColorModelRadioButtonTheme", "RadioButton"),
                     ("ColorViewNumericUpDownTheme", "NumericUpDown")
                 })
        {
            var localTheme = dictionary.Elements(avalonia + "ControlTheme")
                .Single(item => (string?)item.Attribute(xaml + "Key") == themeKey);
            Assert.AreEqual(targetType, (string?)localTheme.Attribute("TargetType"));
            Assert.IsTrue(localTheme.Elements(avalonia + "Setter")
                .Any(setter => (string?)setter.Attribute("Property") == "Template"),
                $"The ColorView {targetType} theme must supply its own control template.");
        }

        var numericFields = colorView.Descendants(avalonia + "NumericUpDown").ToList();
        Assert.AreEqual(4, numericFields.Count,
            "ColorView has four numeric component editors.");
        foreach (var numericField in numericFields)
        {
            Assert.AreEqual("{StaticResource ColorViewNumericUpDownTheme}",
                (string?)numericField.Attribute("Theme"));
            Assert.AreEqual("False", (string?)numericField.Attribute("ShowButtonSpinner"));
        }

        var numericTheme = dictionary.Elements(avalonia + "ControlTheme")
            .Single(item => (string?)item.Attribute(xaml + "Key") == "ColorViewNumericUpDownTheme");
        var numericTemplate = numericTheme.Descendants(avalonia + "ControlTemplate").Single();
        Assert.IsTrue(numericTemplate.Descendants(avalonia + "TextBox")
            .Any(textBox => (string?)textBox.Attribute("Name") == "PART_TextBox"
                || (string?)textBox.Attribute(xaml + "Name") == "PART_TextBox"),
            "NumericUpDown must retain the text editor part required for value editing.");
        Assert.IsTrue(numericTemplate.Descendants(avalonia + "ButtonSpinner")
            .Any(spinner => (string?)spinner.Attribute("Name") == "PART_Spinner"),
            "NumericUpDown must retain its spinner hook for AllowSpin input.");
        Assert.IsTrue(numericTemplate.Descendants(avalonia + "TextBox")
            .Any(textBox => ((string?)textBox.Attribute("Text"))?.Contains(
                "{TemplateBinding Text, Mode=TwoWay}", StringComparison.Ordinal) == true));
    }

    [TestMethod]
    public void ColorPickerSharedVisualResourcesUseTheApplicationPalette()
    {
        var root = GetRepositoryRoot();
        var colorPickerThemesPath = Path.Combine(
            root, "Common", "Frontend", "Assets", "Styles", "ColorPickerThemes.axaml");
        var colorPickerThemes = XDocument.Load(colorPickerThemesPath);
        var resourceMarkup = colorPickerThemes.Root!.DescendantsAndSelf()
            .SelectMany(element => element.Attributes())
            .Select(attribute => attribute.Value)
            .Where(value => value.Contains("{DynamicResource ", StringComparison.Ordinal) ||
                            value.Contains("{StaticResource ", StringComparison.Ordinal));

        Assert.IsTrue(resourceMarkup.Any(value =>
            value.Contains("{DynamicResource AppBrush.Selection.Border}", StringComparison.Ordinal)));
        Assert.IsTrue(resourceMarkup.Any(value =>
            value.Contains("{DynamicResource AppMetric.CornerRadius.Control}", StringComparison.Ordinal)));

        var sharedResources = XDocument.Load(Path.Combine(
            root, "Common", "Frontend", "Assets", "Styles", "SharedUiResources.axaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var sharedResourceKeys = sharedResources.Descendants()
            .Attributes(xaml + "Key")
            .Select(attribute => attribute.Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.IsTrue(sharedResources.Descendants()
            .Attributes(xaml + "Key")
            .Any(attribute => attribute.Value == "AppMetric.CornerRadius.Control"),
            "ColorPicker control corners should use the app-owned shared radius metric.");
        Assert.IsTrue(sharedResourceKeys.Contains("ColorSpectrumSelectorLightBrush"));
        Assert.IsTrue(sharedResourceKeys.Contains("ColorSpectrumSelectorDarkBrush"));

        foreach (var themeName in new[] { "Dark", "Light" })
        {
            var themeDictionary = XDocument.Load(Path.Combine(
                root, "Common", "Frontend", "Assets", "Themes", themeName, "Theme.axaml"));
            var keys = themeDictionary.Descendants()
                .Attributes(xaml + "Key")
                .Select(attribute => attribute.Value)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var paletteResource in new[]
                     {
                         "AppBrush.Action.Primary", "AppBrush.Border.Default", "AppBrush.Border.Emphasis",
                         "AppBrush.Navigation.Selected", "AppBrush.Selection.Background", "AppBrush.Selection.Border",
                         "AppBrush.Selection.HoverBackground", "AppBrush.Selection.HoverBorder",
                         "AppBrush.Surface.Elevated", "AppBrush.Surface.Hover", "AppBrush.Surface.Input",
                         "AppBrush.Surface.Pressed", "AppBrush.Text.Disabled", "AppBrush.Text.Primary",
                         "AppBrush.Text.Secondary"
                     })
            {
                Assert.IsTrue(keys.Contains(paletteResource),
                    $"{themeName} is missing the ColorPicker palette resource {paletteResource}.");
            }
        }
    }

    [TestMethod]
    public void ColorSpectrumUsesAnApplicationOwnedTemplateAndPreservesItsInteractionParts()
    {
        var root = GetRepositoryRoot();
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var themes = XDocument.Load(Path.Combine(
            root, "Common", "Frontend", "Assets", "Styles", "ColorPickerThemes.axaml"));
        var dictionary = themes.Root!;
        var theme = dictionary.Elements(avalonia + "ControlTheme")
            .Single(item => (string?)item.Attribute(xaml + "Key") == "{x:Type ColorSpectrum}");

        Assert.AreEqual("ColorSpectrum", (string?)theme.Attribute("TargetType"));
        var template = theme.Descendants(avalonia + "ControlTemplate").Single();
        var partNames = template.Descendants()
            .Select(element => (string?)element.Attribute(xaml + "Name"))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        foreach (var requiredPart in new[]
                 {
                     "PART_LayoutRoot",
                     "PART_SizingPanel",
                     "PART_SpectrumRectangle",
                     "PART_SpectrumOverlayRectangle",
                     "PART_SpectrumEllipse",
                     "PART_SpectrumOverlayEllipse",
                     "PART_InputTarget",
                     "PART_SelectionEllipsePanel",
                     "FocusEllipse",
                     "SelectionEllipse",
                     "BorderRectangle",
                     "BorderEllipse"
                 })
        {
            Assert.IsTrue(partNames.Contains(requiredPart),
                $"ColorSpectrum must retain its {requiredPart} template part.");
        }

        var styleSelectors = theme.Descendants(avalonia + "Style")
            .Select(style => (string?)style.Attribute("Selector"))
            .OfType<string>()
            .ToArray();
        foreach (var requiredSelector in new[]
                 {
                     "^:focus-visible /template/ Ellipse#FocusEllipse",
                     "^:light-selector /template/ Ellipse#FocusEllipse",
                     "^:light-selector /template/ Ellipse#SelectionEllipse",
                     "^:pointerover /template/ Ellipse#SelectionEllipse",
                     "^:large-selector /template/ Panel#PART_SelectionEllipsePanel"
                 })
        {
            Assert.IsTrue(styleSelectors.Contains(requiredSelector, StringComparer.Ordinal),
                $"ColorSpectrum must retain its {requiredSelector} state selector.");
        }

        var sharedResources = XDocument.Load(Path.Combine(
            root, "Common", "Frontend", "Assets", "Styles", "SharedUiResources.axaml"));
        var resourceKeys = dictionary.Elements()
            .Concat(sharedResources.Root!.Elements())
            .Select(element => (string?)element.Attribute(xaml + "Key"))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        foreach (var requiredResource in new[]
                 {
                     "EnumToBoolConverter",
                     "ColorSpectrumTopLeftCornerRadiusConverter",
                     "ColorSpectrumBottomRightCornerRadiusConverter",
                     "ColorSpectrumSelectorLightBrush",
                     "ColorSpectrumSelectorDarkBrush"
                 })
        {
            Assert.IsTrue(resourceKeys.Contains(requiredResource),
                $"ColorSpectrum must use the app-owned {requiredResource} resource.");
        }

        var themeText = theme.ToString();
        StringAssert.Contains(themeText, "DynamicResource AppBrush.Border.Subtle");
    }

    [TestMethod]
    public void ColorPreviewerUsesApplicationOwnedTemplateAndCheckeredBrush()
    {
        var root = GetRepositoryRoot();
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var themes = XDocument.Load(Path.Combine(
            root, "Common", "Frontend", "Assets", "Styles", "ColorPickerThemes.axaml"));
        var dictionary = themes.Root!;
        var theme = dictionary.Elements(avalonia + "ControlTheme")
            .Single(item => (string?)item.Attribute(xaml + "Key") == "{x:Type ColorPreviewer}");

        Assert.AreEqual("ColorPreviewer", (string?)theme.Attribute("TargetType"));
        var template = theme.Descendants(avalonia + "ControlTemplate").Single();
        var partNames = template.Descendants()
            .Select(element => (string?)element.Attribute(xaml + "Name"))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        foreach (var requiredPart in new[]
                 {
                     "PART_AccentDecrement2Border",
                     "PART_AccentDecrement1Border",
                     "PART_AccentIncrement1Border",
                     "PART_AccentIncrement2Border"
                 })
        {
            Assert.IsTrue(partNames.Contains(requiredPart),
                $"ColorPreviewer must retain its {requiredPart} template part.");
        }

        var resourceKeys = dictionary.Elements()
            .Select(element => (string?)element.Attribute(xaml + "Key"))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        foreach (var requiredResource in new[]
                 {
                     "AppColorPreviewerAccentColorConverter",
                     "AppColorPreviewerToBrushConverter",
                     "ColorPreviewerLeftCornerRadiusFilterConverter",
                     "ColorPreviewerRightCornerRadiusFilterConverter",
                     "ColorPreviewerAccentSectionWidth",
                     "ColorPreviewerAccentSectionHeight",
                     "AppColorPickerCheckeredBackgroundBrush"
                 })
        {
            Assert.IsTrue(resourceKeys.Contains(requiredResource),
                $"ColorPreviewer must use the app-owned {requiredResource} resource.");
        }

        var cornerRadiusSetter = theme.Elements(avalonia + "Setter")
            .Single(setter => (string?)setter.Attribute("Property") == "CornerRadius");
        Assert.AreEqual("{DynamicResource AppMetric.CornerRadius.Control}",
            (string?)cornerRadiusSetter.Attribute("Value"));
        Assert.IsTrue(template.Descendants(avalonia + "Border")
            .Any(border => (string?)border.Attribute("BoxShadow") == "0 0 10 2 #BF000000"),
            "The main color preview should retain its drop shadow.");

        var templateAttributeValues = template.DescendantsAndSelf()
            .SelectMany(element => element.Attributes())
            .Select(attribute => attribute.Value)
            .ToArray();
        Assert.IsTrue(templateAttributeValues.Any(value =>
            value.Contains("AppColorPickerCheckeredBackgroundBrush", StringComparison.Ordinal)));
        Assert.IsFalse(templateAttributeValues.Any(value =>
            value.Contains("{StaticResource AccentColorConverter}", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void TransitioningContentControlUsesAnApplicationOwnedTemplateWithBothPresenterSlots()
    {
        var root = GetRepositoryRoot();
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var themes = XDocument.Load(Path.Combine(
            root, "Common", "Frontend", "Assets", "Styles", "ControlThemes.axaml"));
        var theme = themes.Root!.Elements(avalonia + "ControlTheme")
            .Single(item => (string?)item.Attribute(xaml + "Key") ==
                "{x:Type TransitioningContentControl}");

        Assert.AreEqual("TransitioningContentControl", (string?)theme.Attribute("TargetType"));
        var template = theme.Descendants(avalonia + "ControlTemplate").Single();
        var host = template.Elements(avalonia + "Grid").Single();
        Assert.AreEqual("{TemplateBinding ClipToBounds}", (string?)host.Attribute("ClipToBounds"));

        var presenters = host.Elements(avalonia + "ContentPresenter").ToArray();
        Assert.AreEqual(2, presenters.Length,
            "The transition control needs two presenters to animate between old and new content.");
        Assert.AreEqual("PART_ContentPresenter", (string?)presenters[0].Attribute("Name"));
        Assert.AreEqual("PART_ContentPresenter2", (string?)presenters[1].Attribute("Name"));
        foreach (var presenter in presenters)
        {
            Assert.AreEqual("{TemplateBinding ContentTemplate}",
                (string?)presenter.Attribute("ContentTemplate"));
            Assert.AreEqual("{TemplateBinding Padding}", (string?)presenter.Attribute("Padding"));
            Assert.AreEqual("{TemplateBinding HorizontalContentAlignment}",
                (string?)presenter.Attribute("HorizontalContentAlignment"));
            Assert.AreEqual("{TemplateBinding VerticalContentAlignment}",
                (string?)presenter.Attribute("VerticalContentAlignment"));
            Assert.IsNull(presenter.Attribute("Content"),
                "TransitioningContentControl assigns content to each presenter as it changes.");
        }

        var adaptiveControl = File.ReadAllText(Path.Combine(
            root, "Common", "Frontend", "Views", "Controls", "AdaptiveTransitioningContentControl.cs"));
        StringAssert.Contains(adaptiveControl, "StyleKeyOverride => typeof(TransitioningContentControl)");
        StringAssert.Contains(adaptiveControl, "PageTransition = null");
        StringAssert.Contains(adaptiveControl, "PageTransition = AnimationsEnabled ? _transition : null");

        var viewFiles = Directory.GetFiles(
            Path.Combine(root, "Common", "Frontend", "Views"), "*.axaml", SearchOption.AllDirectories);
        Assert.AreEqual(4, viewFiles.Sum(path =>
            XDocument.Load(path).Descendants()
                .Count(element => element.Name.LocalName == "AdaptiveTransitioningContentControl")),
            "All current animated view hosts use the app's shared transition-control theme.");
    }

    [TestMethod]
    public void ContentControlUsesAnApplicationOwnedTemplateWithTheFrameworkPresenterContract()
    {
        var root = GetRepositoryRoot();
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var themes = XDocument.Load(Path.Combine(
            root, "Common", "Frontend", "Assets", "Styles", "ControlThemes.axaml"));
        var theme = themes.Root!.Elements(avalonia + "ControlTheme")
            .Single(item => (string?)item.Attribute(xaml + "Key") == "{x:Type ContentControl}");

        Assert.AreEqual("ContentControl", (string?)theme.Attribute("TargetType"));
        var presenter = theme.Descendants(avalonia + "ContentPresenter").Single();
        Assert.AreEqual("PART_ContentPresenter", (string?)presenter.Attribute("Name"));
        foreach (var property in new[]
                 {
                     "Background",
                     "BackgroundSizing",
                     "BorderBrush",
                     "BorderThickness",
                     "CornerRadius",
                     "ContentTemplate",
                     "Content",
                     "Padding",
                     "VerticalContentAlignment",
                     "HorizontalContentAlignment"
                 })
        {
            Assert.AreEqual($"{{TemplateBinding {property}}}",
                (string?)presenter.Attribute(property),
                $"The ContentControl presenter must forward {property}.");
        }

        var viewFiles = Directory.GetFiles(
            Path.Combine(root, "Common", "Frontend", "Views"), "*.axaml", SearchOption.AllDirectories);
        Assert.AreEqual(3, viewFiles.Sum(path =>
            XDocument.Load(path).Descendants().Count(element => element.Name.LocalName == "ContentControl")),
            "All current view-level content hosts should use the shared app-owned theme.");
    }

    [TestMethod]
    public void WindowUsesAnApplicationOwnedTemplateAndPreservesDecorationContentContracts()
    {
        var root = GetRepositoryRoot();
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var themes = XDocument.Load(Path.Combine(
            root, "Common", "Frontend", "Assets", "Styles", "ControlThemes.axaml"));
        var windowTheme = themes.Root!.Elements(avalonia + "ControlTheme")
            .Single(theme => (string?)theme.Attribute(xaml + "Key") == "{x:Type Window}");

        Assert.AreEqual("Window", (string?)windowTheme.Attribute("TargetType"));
        var setters = windowTheme.Elements(avalonia + "Setter")
            .ToDictionary(setter => (string)setter.Attribute("Property")!,
                setter => (string?)setter.Attribute("Value"), StringComparer.Ordinal);
        Assert.AreEqual("{DynamicResource AppBrush.Background.Primary}", setters["Background"]);
        Assert.AreEqual("{DynamicResource AppBrush.Background.Primary}",
            setters["TransparencyBackgroundFallback"]);
        Assert.AreEqual("{DynamicResource AppBrush.Background.Primary}", setters["TopLevel.SystemBarColor"]);
        Assert.AreEqual("{DynamicResource AppBrush.Text.Primary}", setters["Foreground"]);
        Assert.AreEqual("14", setters["FontSize"]);
        Assert.AreEqual("fonts:Inter#Inter, $Default", setters["FontFamily"]);

        var template = windowTheme.Descendants(avalonia + "ControlTemplate").Single();
        var transparencyFallback = template.Descendants(avalonia + "Border")
            .Single(border => (string?)border.Attribute("Name") == "PART_TransparencyFallback");
        Assert.AreEqual("False", (string?)transparencyFallback.Attribute("IsHitTestVisible"));
        var windowBackground = template.Descendants(avalonia + "Border")
            .Single(border => (string?)border.Attribute("Background") == "{TemplateBinding Background}");
        Assert.AreEqual("False", (string?)windowBackground.Attribute("IsHitTestVisible"));
        var decorationMarginHost = template.Descendants(avalonia + "Panel")
            .Single(panel => (string?)panel.Attribute("Margin") == "{TemplateBinding WindowDecorationMargin}");
        Assert.AreEqual("Transparent", (string?)decorationMarginHost.Attribute("Background"));

        var visualLayerManager = template.Descendants(avalonia + "VisualLayerManager")
            .Single(manager => (string?)manager.Attribute("Name") == "PART_VisualLayerManager");
        var contentPresenter = visualLayerManager.Elements(avalonia + "ContentPresenter")
            .Single(presenter => (string?)presenter.Attribute("Name") == "PART_ContentPresenter");
        Assert.AreEqual("{TemplateBinding Content}", (string?)contentPresenter.Attribute("Content"));
        Assert.AreEqual("{TemplateBinding ContentTemplate}", (string?)contentPresenter.Attribute("ContentTemplate"));
        Assert.AreEqual("{TemplateBinding Padding}", (string?)contentPresenter.Attribute("Margin"));
        Assert.AreEqual("{TemplateBinding HorizontalContentAlignment}",
            (string?)contentPresenter.Attribute("HorizontalContentAlignment"));
        Assert.AreEqual("{TemplateBinding VerticalContentAlignment}",
            (string?)contentPresenter.Attribute("VerticalContentAlignment"));
        var viewFiles = Directory.GetFiles(
            Path.Combine(root, "Common", "Frontend", "Views"), "*.axaml", SearchOption.AllDirectories);
        Assert.AreEqual(2, viewFiles.Sum(path => XDocument.Load(path).Descendants(avalonia + "Window").Count()),
            "The audit expects both currently used Window controls to receive the app-owned default theme.");
    }

    [TestMethod]
    public void ItemsControlUsesAnApplicationOwnedPresenterAndPreservesCurrentItemLayouts()
    {
        var root = GetRepositoryRoot();
        var themesPath = Path.Combine(root, "Common", "Frontend", "Assets", "Styles", "ControlThemes.axaml");
        var themes = XDocument.Load(themesPath);
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var itemsControlTheme = themes.Root!.Elements(avalonia + "ControlTheme")
            .Single(theme => (string?)theme.Attribute(xaml + "Key") == "{x:Type ItemsControl}");

        Assert.AreEqual("ItemsControl", (string?)itemsControlTheme.Attribute("TargetType"));
        var template = itemsControlTheme.Descendants(avalonia + "ControlTemplate").Single();
        var presenter = template.Elements(avalonia + "ItemsPresenter").Single();
        Assert.AreEqual("PART_ItemsPresenter", (string?)presenter.Attribute("Name"));
        Assert.AreEqual("{TemplateBinding ItemsPanel}", (string?)presenter.Attribute("ItemsPanel"));
        Assert.AreEqual("{TemplateBinding Padding}", (string?)presenter.Attribute("Margin"));
        var viewRoot = Path.Combine(root, "Common", "Frontend", "Views");
        var viewFiles = Directory.EnumerateFiles(viewRoot, "*.axaml", SearchOption.AllDirectories).ToArray();
        var viewItemsControls = viewFiles.SelectMany(file =>
            XDocument.Load(file).Descendants(avalonia + "ItemsControl")).ToArray();
        Assert.AreEqual(12, viewItemsControls.Length,
            "All current view ItemsControls should receive the application-owned default theme.");
        Assert.IsTrue(viewItemsControls.All(control => control.Attribute("Theme") is null),
            "View ItemsControls should inherit the application-owned default theme.");
        Assert.IsTrue(viewItemsControls.All(control =>
            control.Attribute("ItemsSource") is not null &&
            control.Element(avalonia + "ItemsControl.ItemTemplate") is not null),
            "All current ItemsControls should keep their data source and item template.");

        var customPanels = viewItemsControls.Select(control =>
            control.Element(avalonia + "ItemsControl.ItemsPanel")?
                .Element(avalonia + "ItemsPanelTemplate")?.Elements().Single().Name.LocalName)
            .Where(panel => panel is not null).ToArray();
        Assert.AreEqual(4, customPanels.Count(panel => panel == "VirtualizingStackPanel"));
        Assert.AreEqual(2, customPanels.Count(panel => panel == "WrapPanel"));
        Assert.AreEqual(6, viewItemsControls.Length - customPanels.Length,
            "ItemsControls without an explicit panel should retain ItemsControl's default panel.");
    }

    [TestMethod]
    public void CarouselUsesAnApplicationOwnedScrollingPresenterAndRetainsMobileNavigationBindings()
    {
        var root = GetRepositoryRoot();
        var themesPath = Path.Combine(root, "Common", "Frontend", "Assets", "Styles", "ControlThemes.axaml");
        var themes = XDocument.Load(themesPath);
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var carouselTheme = themes.Root!.Elements(avalonia + "ControlTheme")
            .Single(theme => (string?)theme.Attribute(xaml + "Key") == "{x:Type Carousel}");

        Assert.AreEqual("Carousel", (string?)carouselTheme.Attribute("TargetType"));
        var template = carouselTheme.Descendants(avalonia + "ControlTemplate").Single();
        var rootGrid = template.Elements(avalonia + "Grid").Single();
        Assert.AreEqual("{TemplateBinding ClipToBounds}", (string?)rootGrid.Attribute("ClipToBounds"));
        var scrollViewer = rootGrid.Elements(avalonia + "ScrollViewer").Single();
        Assert.AreEqual("PART_ScrollViewer", (string?)scrollViewer.Attribute("Name"));
        Assert.AreEqual("Hidden", (string?)scrollViewer.Attribute("HorizontalScrollBarVisibility"));
        Assert.AreEqual("Hidden", (string?)scrollViewer.Attribute("VerticalScrollBarVisibility"));
        Assert.AreEqual("False", (string?)scrollViewer.Attribute("IsTabStop"));
        var itemsPresenter = scrollViewer.Elements(avalonia + "ItemsPresenter").Single();
        Assert.AreEqual("PART_ItemsPresenter", (string?)itemsPresenter.Attribute("Name"));
        Assert.AreEqual("{TemplateBinding ItemsPanel}", (string?)itemsPresenter.Attribute("ItemsPanel"));
        Assert.AreEqual("{TemplateBinding Padding}", (string?)itemsPresenter.Attribute("Margin"));
        var viewRoot = Path.Combine(root, "Common", "Frontend", "Views");
        var viewFiles = Directory.EnumerateFiles(viewRoot, "*.axaml", SearchOption.AllDirectories).ToArray();
        var carousels = viewFiles.SelectMany(file =>
            XDocument.Load(file).Descendants(avalonia + "Carousel")).ToArray();
        var carousel = carousels.Single();
        Assert.IsNull(carousel.Attribute("Theme"),
            "The mobile main Carousel should inherit the application-owned default theme.");
        Assert.IsNotNull(carousel.Attribute("ItemsSource"));
        Assert.AreEqual("{Binding CurrentMobileNavigationIndex, Mode=TwoWay}",
            (string?)carousel.Attribute("SelectedIndex"));
        Assert.IsNotNull(carousel.Attribute("IsSwipeEnabled"));
        Assert.AreEqual("True", (string?)carousel.Attribute("WrapSelection"));
        Assert.AreEqual("1", (string?)carousel.Attribute("ViewportFraction"));
        Assert.AreEqual("True", (string?)carousel.Attribute("ClipToBounds"));
    }

    [TestMethod]
    public void PathIconUsesAnApplicationOwnedTemplateAndPreservesInheritedForeground()
    {
        var root = GetRepositoryRoot();
        var themesPath = Path.Combine(root, "Common", "Frontend", "Assets", "Styles", "ControlThemes.axaml");
        var themes = XDocument.Load(themesPath);
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var pathIconTheme = themes.Root!.Elements(avalonia + "ControlTheme")
            .Single(theme => (string?)theme.Attribute(xaml + "Key") == "{x:Type PathIcon}");

        Assert.AreEqual("PathIcon", (string?)pathIconTheme.Attribute("TargetType"));
        Assert.IsFalse(pathIconTheme.Elements(avalonia + "Setter").Any(setter =>
            (string?)setter.Attribute("Property") == "Foreground"),
            "PathIcon should inherit Foreground from its parent unless an instance overrides it.");
        var template = pathIconTheme.Descendants(avalonia + "ControlTemplate").Single();
        var viewbox = template.Elements(avalonia + "Viewbox").Single();
        Assert.AreEqual("Uniform", (string?)viewbox.Attribute("Stretch"));
        var path = viewbox.Elements(avalonia + "Path").Single();
        Assert.AreEqual("{TemplateBinding Data}", (string?)path.Attribute("Data"));
        Assert.AreEqual("{TemplateBinding Foreground}", (string?)path.Attribute("Fill"));
        Assert.AreEqual("Uniform", (string?)path.Attribute("Stretch"));
        var viewRoot = Path.Combine(root, "Common", "Frontend", "Views");
        var viewFiles = Directory.EnumerateFiles(viewRoot, "*.axaml", SearchOption.AllDirectories).ToArray();
        var viewIcons = viewFiles.SelectMany(file =>
                XDocument.Load(file).Descendants(avalonia + "PathIcon")
                    .Select(icon => (File: file, Icon: icon)))
            .ToArray();
        Assert.IsTrue(viewIcons.Length > 0,
            "The view icon inventory should be non-empty so the per-icon theme checks execute.");
        foreach (var (file, icon) in viewIcons)
        {
            var location = $"{Path.GetRelativePath(viewRoot, file)} PathIcon ({(string?)icon.Attribute("Data") ?? "no Data"})";
            Assert.IsNull(icon.Attribute("Theme"),
                $"{location} should use the application-owned default PathIcon theme.");

            var foreground = (string?)icon.Attribute("Foreground");
            if (foreground is not null)
            {
                Assert.AreEqual("{DynamicResource AppBrush.Status.Danger}", foreground,
                    $"{location} has an explicit semantic foreground override; other view icons inherit Foreground.");
            }
        }

        var scrollBarTheme = themes.Root.Elements(avalonia + "ControlTheme")
            .Single(theme => (string?)theme.Attribute(xaml + "Key") == "{x:Type ScrollBar}");
        Assert.AreEqual(4, scrollBarTheme.Descendants(avalonia + "PathIcon").Count(),
            "The four ScrollBar arrow glyphs should use the application-owned PathIcon theme.");
    }

    [TestMethod]
    public void ExistingButtonVariantsKeepTheirLayoutIntentOnTheSharedTemplate()
    {
        var root = GetRepositoryRoot();
        var stylesPath = Path.Combine(root, "Common", "Frontend", "Assets", "Styles", "CommonStyles.axaml");
        var styles = XDocument.Load(stylesPath);
        XNamespace avalonia = "https://github.com/avaloniaui";
        var buttonVariants = styles.Descendants(avalonia + "Style")
            .Where(style => ((string?)style.Attribute("Selector")) is
                "Button.listItemContentButton" or "Button.mainNavigation")
            .ToArray();

        Assert.AreEqual(2, buttonVariants.Length);
        foreach (var style in buttonVariants)
        {
            Assert.IsFalse(style.Elements(avalonia + "Setter").Any(setter =>
                (string?)setter.Attribute("Property") == "Template"));
        }
        Assert.IsTrue(HasSetter(buttonVariants[0], avalonia,
            "HorizontalContentAlignment", "Stretch"));
        Assert.IsTrue(HasSetter(buttonVariants[1], avalonia,
            "CornerRadius", "8"));
    }

    [TestMethod]
    public void WindowsMainNavigationHasDistinctSelectedAndHoverStates()
    {
        var root = GetRepositoryRoot();
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var viewPath = Path.Combine(root, "Common", "Frontend", "Views", "MainView.axaml");
        var view = XDocument.Load(viewPath);
        var navigationPanel = view.Descendants(avalonia + "WrapPanel")
            .Single(panel => (string?)panel.Attribute(xaml + "Name") == "DesktopMainNavigationButtons");
        var navigationButtons = navigationPanel.Elements(avalonia + "Button")
            .Where(button => ((string?)button.Attribute("Classes"))?.Split(' ')
                .Contains("mainNavigation", StringComparer.Ordinal) == true)
            .ToArray();
        Assert.AreEqual(3, navigationButtons.Length);
        Assert.IsTrue(navigationButtons.All(button =>
            ((string?)button.Attribute("Classes"))?.Split(' ')
                .Contains("ghost", StringComparer.Ordinal) == true));

        var codeBehind = File.ReadAllText(Path.Combine(
            root, "Common", "Frontend", "Views", "MainView.axaml.cs"));
        StringAssert.Contains(codeBehind, "ConfigureWindowsMainNavigationVisualStates();");
        StringAssert.Contains(codeBehind, "if (!OperatingSystem.IsWindows())");
        StringAssert.Contains(codeBehind,
            "DesktopMainNavigationButtons.Children.OfType<Button>()");
        StringAssert.Contains(codeBehind, "button.Classes.Add(\"windowsMainNavigation\")");

        var stylesPath = Path.Combine(
            root, "Common", "Frontend", "Assets", "Styles", "CommonStyles.axaml");
        var styles = XDocument.Load(stylesPath);
        var baseNavigationStyle = styles.Descendants(avalonia + "Style")
            .Single(style => (string?)style.Attribute("Selector") == "Button.mainNavigation");
        Assert.IsTrue(HasSetter(baseNavigationStyle, avalonia, "BorderThickness", "2"));
        Assert.IsTrue(HasSetter(baseNavigationStyle, avalonia, "Background", "Transparent"));
        Assert.IsTrue(HasSetter(baseNavigationStyle, avalonia, "BorderBrush", "Transparent"));
        var navigationStyles = styles.Descendants(avalonia + "Style")
            .Where(style => ((string?)style.Attribute("Selector"))?.Contains(
                "windowsMainNavigation", StringComparison.Ordinal) == true)
            .ToDictionary(style => (string)style.Attribute("Selector")!, StringComparer.Ordinal);
        AssertNavigationStyle(navigationStyles,
            "Button.mainNavigation.windowsMainNavigation",
            "BorderThickness", "1");
        AssertNavigationStyle(navigationStyles,
            "Button.mainNavigation.windowsMainNavigation:pointerover",
            "BorderThickness", "1");
        AssertNavigationStyle(navigationStyles,
            "Button.mainNavigation.windowsMainNavigation:pointerover",
            "Background", "{DynamicResource AppBrush.Surface.Hover}");
        AssertNavigationStyle(navigationStyles,
            "Button.mainNavigation.windowsMainNavigation:pointerover",
            "BorderBrush", "{DynamicResource AppBrush.Border.Muted}");
        AssertNavigationStyle(navigationStyles,
            "Button.mainNavigation.windowsMainNavigation.selected",
            "BorderThickness", "1");
        AssertNavigationStyle(navigationStyles,
            "Button.mainNavigation.windowsMainNavigation.selected",
            "Background", "{DynamicResource AppBrush.Selection.Background}");
        AssertNavigationStyle(navigationStyles,
            "Button.mainNavigation.windowsMainNavigation.selected",
            "BorderBrush", "{DynamicResource AppBrush.Navigation.SelectedBorder}");
        AssertNavigationStyle(navigationStyles,
            "Button.mainNavigation.windowsMainNavigation.selected:pointerover",
            "BorderThickness", "1");
        AssertNavigationStyle(navigationStyles,
            "Button.mainNavigation.windowsMainNavigation.selected:pointerover",
            "Background", "{DynamicResource AppBrush.Navigation.Hover}");
        AssertNavigationStyle(navigationStyles,
            "Button.mainNavigation.windowsMainNavigation.selected:pointerover",
            "BorderBrush", "{DynamicResource AppBrush.Selection.Border}");

        foreach (var themeName in new[] { "Dark", "Light" })
        {
            var themeDictionary = XDocument.Load(Path.Combine(
                root, "Common", "Frontend", "Assets", "Themes", themeName, "Theme.axaml"));
            var resourceKeys = themeDictionary.Descendants()
                .Attributes(xaml + "Key")
                .Select(attribute => attribute.Value)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var resource in new[]
                     {
                         "AppBrush.Surface.Hover",
                         "AppBrush.Border.Muted",
                         "AppBrush.Selection.Background",
                         "AppBrush.Navigation.SelectedBorder",
                         "AppBrush.Navigation.Hover",
                         "AppBrush.Selection.Border"
                     })
            {
                Assert.IsTrue(resourceKeys.Contains(resource),
                    $"{themeName} is missing Windows main-navigation resource {resource}.");
            }
        }
    }

    private static void AssertNavigationStyle(
        IReadOnlyDictionary<string, XElement> styles,
        string selector,
        string property,
        string value)
    {
        Assert.IsTrue(styles.TryGetValue(selector, out var style),
            $"CommonStyles.axaml is missing Windows navigation state {selector}.");
        Assert.IsTrue(HasSetter(style!, XNamespace.Get("https://github.com/avaloniaui"), property, value),
            $"Windows navigation state {selector} must set {property} to {value}.");
    }

    [TestMethod]
    public void ProgressBarUsesAnApplicationOwnedThemeForDeterminateAndIndeterminateStates()
    {
        var root = GetRepositoryRoot();
        var themesPath = Path.Combine(root, "Common", "Frontend", "Assets", "Styles", "ControlThemes.axaml");
        var themes = XDocument.Load(themesPath);
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var progressTheme = themes.Root!.Elements(avalonia + "ControlTheme")
            .Single(theme => (string?)theme.Attribute(xaml + "Key") == "{x:Type ProgressBar}");

        Assert.AreEqual("ProgressBar", (string?)progressTheme.Attribute("TargetType"));
        var template = progressTheme.Descendants(avalonia + "ControlTemplate").Single();
        var indicator = template.Descendants(avalonia + "Border")
            .Single(border => (string?)border.Attribute("Name") == "PART_Indicator");
        Assert.AreEqual("PART_Indicator", (string?)indicator.Attribute("Name"));
        Assert.AreEqual("Stretch", (string?)indicator.Attribute("VerticalAlignment"));
        var indeterminateIndicator = template.Descendants(avalonia + "Border")
            .Single(border => (string?)border.Attribute("Name") == "PART_IndeterminateIndicator");
        Assert.AreEqual("Stretch", (string?)indeterminateIndicator.Attribute("VerticalAlignment"));

        var verticalDeterminateStyle = progressTheme.Elements(avalonia + "Style")
            .Single(style => (string?)style.Attribute("Selector") ==
                "^:vertical /template/ Border#PART_Indicator");
        Assert.IsTrue(HasSetter(verticalDeterminateStyle, avalonia, "HorizontalAlignment", "Stretch"));
        Assert.IsTrue(HasSetter(verticalDeterminateStyle, avalonia, "VerticalAlignment", "Bottom"));
        var verticalIndeterminateStyle = progressTheme.Elements(avalonia + "Style")
            .Single(style => (string?)style.Attribute("Selector") ==
                "^:vertical:indeterminate /template/ Border#PART_IndeterminateIndicator");
        Assert.IsTrue(HasSetter(verticalIndeterminateStyle, avalonia, "HorizontalAlignment", "Stretch"));
        Assert.IsTrue(HasSetter(verticalIndeterminateStyle, avalonia, "VerticalAlignment", "Top"));

        var themeText = progressTheme.ToString(SaveOptions.DisableFormatting);
        foreach (var requiredTemplateFeature in new[]
                 {
                     "PART_IndeterminateIndicator",
                     "TemplateSettings.ContainerWidth",
                     "TemplateSettings.IndeterminateStartingOffset",
                     "TemplateSettings.IndeterminateEndingOffset",
                     "TranslateTransform.X",
                     "TranslateTransform.Y"
                 })
        {
            StringAssert.Contains(themeText, requiredTemplateFeature);
        }

        foreach (var stateSelector in new[]
                 {
                     ":horizontal:indeterminate",
                     ":vertical:indeterminate",
                     ":disabled"
                 })
        {
            StringAssert.Contains(themeText, stateSelector);
        }

        var requiredResources = new[]
        {
            "AppBrush.Action.Primary",
            "AppBrush.Border.Subtle",
            "AppBrush.Surface.SubtleEmphasis",
            "AppBrush.Text.Disabled"
        };
        foreach (var resource in requiredResources)
            StringAssert.Contains(themeText, resource);

        foreach (var themeName in new[] { "Dark", "Light" })
        {
            var themeDictionary = XDocument.Load(Path.Combine(
                root, "Common", "Frontend", "Assets", "Themes", themeName, "Theme.axaml"));
            var resourceKeys = themeDictionary.Descendants()
                .Attributes(xaml + "Key")
                .Select(attribute => attribute.Value)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var resource in requiredResources)
            {
                Assert.IsTrue(resourceKeys.Contains(resource),
                    $"{themeName} is missing the ProgressBar theme resource {resource}.");
            }
        }

        var viewFiles = Directory.GetFiles(
            Path.Combine(root, "Common", "Frontend", "Views"), "*.axaml", SearchOption.AllDirectories);
        var progressBars = viewFiles.SelectMany(path => XDocument.Load(path).Descendants(avalonia + "ProgressBar"))
            .ToArray();
        Assert.IsTrue(progressBars.Length > 0, "The source audit expects ProgressBar controls in the UI.");
        Assert.IsTrue(progressBars.All(progressBar => (string?)progressBar.Attribute("IsIndeterminate") == "True"),
            "The current ProgressBar usages are indeterminate indicators.");
    }

    [TestMethod]
    public void ToolTipUsesAnApplicationOwnedThemeWithBoundedWrappingContent()
    {
        var root = GetRepositoryRoot();
        var themesPath = Path.Combine(root, "Common", "Frontend", "Assets", "Styles", "ControlThemes.axaml");
        var themes = XDocument.Load(themesPath);
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var toolTipTheme = themes.Root!.Elements(avalonia + "ControlTheme")
            .Single(theme => (string?)theme.Attribute(xaml + "Key") == "{x:Type ToolTip}");

        Assert.AreEqual("ToolTip", (string?)toolTipTheme.Attribute("TargetType"));
        Assert.IsTrue(HasSetter(toolTipTheme, avalonia, "MaxWidth", "360"));
        var template = toolTipTheme.Descendants(avalonia + "ControlTemplate").Single();
        Assert.AreEqual(1, template.Descendants(avalonia + "Border").Count());
        var contentPresenter = template.Descendants(avalonia + "ContentPresenter")
            .Single(presenter => (string?)presenter.Attribute("Name") == "PART_ContentPresenter");
        foreach (var requiredBinding in new[]
                 {
                     "{TemplateBinding Content}",
                     "{TemplateBinding ContentTemplate}",
                     "{TemplateBinding Foreground}",
                     "{TemplateBinding Padding}",
                     "{TemplateBinding HorizontalContentAlignment}",
                     "{TemplateBinding VerticalContentAlignment}"
                 })
        {
            Assert.IsTrue(contentPresenter.Attributes().Any(attribute => attribute.Value == requiredBinding),
                $"The ToolTip presenter is missing {requiredBinding}.");
        }
        Assert.AreEqual("Wrap", (string?)contentPresenter.Attribute("TextWrapping"));

        var themeText = toolTipTheme.ToString(SaveOptions.DisableFormatting);
        var requiredResources = new[]
        {
            "AppBrush.Surface.Elevated",
            "AppBrush.Text.Primary",
            "AppBrush.Border.Default"
        };
        foreach (var resource in requiredResources)
            StringAssert.Contains(themeText, resource);

        foreach (var themeName in new[] { "Dark", "Light" })
        {
            var themeDictionary = XDocument.Load(Path.Combine(
                root, "Common", "Frontend", "Assets", "Themes", themeName, "Theme.axaml"));
            var resourceKeys = themeDictionary.Descendants()
                .Attributes(xaml + "Key")
                .Select(attribute => attribute.Value)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var resource in requiredResources)
            {
                Assert.IsTrue(resourceKeys.Contains(resource),
                    $"{themeName} is missing the ToolTip theme resource {resource}.");
            }
        }

        var viewFiles = Directory.GetFiles(
            Path.Combine(root, "Common", "Frontend", "Views"), "*.axaml", SearchOption.AllDirectories);
        var viewDocuments = viewFiles.Select(XDocument.Load).ToArray();
        Assert.IsTrue(viewDocuments.Any(document => document.Descendants().Attributes()
                .Any(attribute => attribute.Name.LocalName == "ToolTip.Tip")),
            "The source audit expects attached ToolTip.Tip usages in the UI.");
        Assert.IsTrue(viewDocuments.Any(document => document.Descendants().Attributes()
                .Any(attribute => attribute.Name.LocalName == "ToolTip.ShowOnDisabled" &&
                    attribute.Value == "True")),
            "The source audit expects disabled controls that intentionally retain their ToolTips.");
    }

    [TestMethod]
    public void TabItemUsesAnApplicationOwnedThemeAndPreservesProfileHeadersAndSelection()
    {
        var root = GetRepositoryRoot();
        var themesPath = Path.Combine(root, "Common", "Frontend", "Assets", "Styles", "ControlThemes.axaml");
        var themes = XDocument.Load(themesPath);
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var tabItemTheme = themes.Root!.Elements(avalonia + "ControlTheme")
            .Single(theme => (string?)theme.Attribute(xaml + "Key") == "{x:Type TabItem}");

        Assert.AreEqual("TabItem", (string?)tabItemTheme.Attribute("TargetType"));
        Assert.IsTrue(HasSetter(tabItemTheme, avalonia, "FocusAdorner", "{x:Null}"));
        Assert.IsTrue(HasSetter(tabItemTheme, avalonia, "MinHeight", "{StaticResource AppMetric.TouchTarget.Standard}"));

        var template = tabItemTheme.Descendants(avalonia + "ControlTemplate").Single();
        var rootBorder = template.Descendants(avalonia + "Border")
            .Single(border => (string?)border.Attribute("Name") == "PART_LayoutRoot");
        StringAssert.Contains(rootBorder.ToString(SaveOptions.DisableFormatting), "{TemplateBinding Background}");
        StringAssert.Contains(rootBorder.ToString(SaveOptions.DisableFormatting), "{TemplateBinding Padding}");
        var headerPresenter = template.Descendants(avalonia + "ContentPresenter")
            .Single(presenter => (string?)presenter.Attribute("Name") == "PART_ContentPresenter");
        Assert.AreEqual("{TemplateBinding Header}", (string?)headerPresenter.Attribute("Content"));
        Assert.AreEqual("{TemplateBinding HeaderTemplate}", (string?)headerPresenter.Attribute("ContentTemplate"));
        Assert.AreEqual("True", (string?)headerPresenter.Attribute("RecognizesAccessKey"));
        Assert.IsTrue(template.Descendants(avalonia + "Border")
            .Any(border => (string?)border.Attribute("Name") == "PART_SelectedPipe"));
        Assert.IsTrue(template.Descendants(avalonia + "Border")
            .Any(border => (string?)border.Attribute("Name") == "PART_FocusRing"));

        var themeText = tabItemTheme.ToString(SaveOptions.DisableFormatting);
        foreach (var stateSelector in new[] { ":selected", ":pointerover", ":pressed", ":disabled", ":focus-visible" })
            StringAssert.Contains(themeText, stateSelector);
        Assert.IsTrue(tabItemTheme.Elements(avalonia + "Style").Any(style =>
            (string?)style.Attribute("Selector") == "^ /template/ Border#PART_LayoutRoot" &&
            HasSetter(style, avalonia, "TextElement.Foreground", "{DynamicResource AppBrush.Text.Secondary}")));
        Assert.IsTrue(tabItemTheme.Elements(avalonia + "Style").Any(style =>
            (string?)style.Attribute("Selector") == "^:disabled /template/ Border#PART_LayoutRoot" &&
            HasSetter(style, avalonia, "TextElement.Foreground", "{DynamicResource AppBrush.Text.Disabled}")));

        var requiredResources = new[]
        {
            "AppBrush.Action.Primary",
            "AppBrush.Focus",
            "AppBrush.Navigation.Hover",
            "AppBrush.Navigation.Selected",
            "AppBrush.Surface.Hover",
            "AppBrush.Surface.Pressed",
            "AppBrush.Text.Disabled",
            "AppBrush.Text.Primary",
            "AppBrush.Text.Secondary"
        };
        foreach (var resource in requiredResources)
            StringAssert.Contains(themeText, resource);

        foreach (var themeName in new[] { "Dark", "Light" })
        {
            var themeDictionary = XDocument.Load(Path.Combine(
                root, "Common", "Frontend", "Assets", "Themes", themeName, "Theme.axaml"));
            var resourceKeys = themeDictionary.Descendants()
                .Attributes(xaml + "Key")
                .Select(attribute => attribute.Value)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var resource in requiredResources)
            {
                Assert.IsTrue(resourceKeys.Contains(resource),
                    $"{themeName} is missing the TabItem theme resource {resource}.");
            }
        }

        var profileTabs = XDocument.Load(Path.Combine(
            root, "Common", "Frontend", "Views", "Pages", "Profile", "ProfileTabsView.axaml"));
        var tabControl = profileTabs.Descendants(avalonia + "TabControl").Single();
        Assert.AreEqual("{Binding SelectedProfileTabIndex, Mode=TwoWay}",
            (string?)tabControl.Attribute("SelectedIndex"));
        var tabItems = tabControl.Elements(avalonia + "TabItem").ToArray();
        Assert.AreEqual(3, tabItems.Length);
        foreach (var tabItem in tabItems)
        {
            Assert.IsTrue(tabItem.Elements(avalonia + "TabItem.Header").Any(),
                "The profile tab's localized, scaled header template must be retained.");
            Assert.AreEqual("compactProfileTab", (string?)tabItem.Attribute("Classes"));
        }
    }

    [TestMethod]
    public void TabControlUsesAnApplicationOwnedHostAndPreservesProfileSelection()
    {
        var root = GetRepositoryRoot();
        var themesPath = Path.Combine(root, "Common", "Frontend", "Assets", "Styles", "ControlThemes.axaml");
        var themes = XDocument.Load(themesPath);
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var tabControlTheme = themes.Root!.Elements(avalonia + "ControlTheme")
            .Single(theme => (string?)theme.Attribute(xaml + "Key") == "{x:Type TabControl}");

        Assert.AreEqual("TabControl", (string?)tabControlTheme.Attribute("TargetType"));
        Assert.IsTrue(HasSetter(tabControlTheme, avalonia, "Margin", "0"));
        Assert.IsTrue(HasSetter(tabControlTheme, avalonia, "Padding", "0"));
        Assert.IsTrue(HasSetter(tabControlTheme, avalonia, "Background", "Transparent"));
        Assert.IsTrue(HasSetter(tabControlTheme, avalonia, "BorderBrush", "{DynamicResource AppBrush.Border.Subtle}"));
        Assert.IsTrue(HasSetter(tabControlTheme, avalonia, "BorderThickness", "0"));

        var template = tabControlTheme.Descendants(avalonia + "ControlTemplate").Single();
        var rootBorder = template.Descendants(avalonia + "Border").Single();
        foreach (var property in new[] { "BorderBrush", "BorderThickness", "CornerRadius", "Background", "HorizontalAlignment", "VerticalAlignment" })
            Assert.AreEqual($"{{TemplateBinding {property}}}", (string?)rootBorder.Attribute(property));

        var itemsPresenter = template.Descendants(avalonia + "ItemsPresenter").Single();
        Assert.AreEqual("PART_ItemsPresenter", (string?)itemsPresenter.Attribute("Name"));
        Assert.AreEqual("{TemplateBinding ItemsPanel}", (string?)itemsPresenter.Attribute("ItemsPanel"));
        Assert.AreEqual("{TemplateBinding TabStripPlacement}", (string?)itemsPresenter.Attribute("DockPanel.Dock"));

        var contentPanel = template.Descendants(avalonia + "Panel")
            .Single(panel => (string?)panel.Attribute("ClipToBounds") == "True");
        var dockPanel = template.Descendants(avalonia + "DockPanel").Single();
        Assert.AreSame(itemsPresenter, dockPanel.Elements().First(),
            "The tab-strip presenter must be docked before the selected-content host.");
        Assert.AreSame(contentPanel, dockPanel.Elements().Last(),
            "The selected-content panel must fill the remaining DockPanel area.");
        var selectedContentHosts = contentPanel.Elements(avalonia + "ContentPresenter").ToArray();
        Assert.AreEqual(2, selectedContentHosts.Length);
        Assert.AreEqual("PART_SelectedContentHost2", (string?)selectedContentHosts[0].Attribute("Name"));
        Assert.AreEqual("False", (string?)selectedContentHosts[0].Attribute("IsVisible"));
        Assert.AreEqual("PART_SelectedContentHost", (string?)selectedContentHosts[1].Attribute("Name"));
        foreach (var host in selectedContentHosts)
        {
            Assert.AreEqual("{TemplateBinding Padding}", (string?)host.Attribute("Margin"));
            Assert.AreEqual("{TemplateBinding HorizontalContentAlignment}",
                (string?)host.Attribute("HorizontalContentAlignment"));
            Assert.AreEqual("{TemplateBinding VerticalContentAlignment}",
                (string?)host.Attribute("VerticalContentAlignment"));
            Assert.IsNull(host.Attribute("Content"),
                "The TabControl framework supplies selected content to its named template host.");
        }

        var leftSelector = "^[TabStripPlacement=Left] /template/ ItemsPresenter#PART_ItemsPresenter > WrapPanel";
        var rightSelector = "^[TabStripPlacement=Right] /template/ ItemsPresenter#PART_ItemsPresenter > WrapPanel";
        foreach (var selector in new[] { leftSelector, rightSelector })
        {
            var placementStyle = tabControlTheme.Elements(avalonia + "Style")
                .Single(style => (string?)style.Attribute("Selector") == selector);
            Assert.IsTrue(HasSetter(placementStyle, avalonia, "Orientation", "Vertical"));
        }

        var topPlacementStyle = tabControlTheme.Elements(avalonia + "Style")
            .Single(style => (string?)style.Attribute("Selector") ==
                "^[TabStripPlacement=Top] /template/ ItemsPresenter#PART_ItemsPresenter");
        Assert.IsTrue(HasSetter(topPlacementStyle, avalonia, "Margin", "0,0,0,2"));

        foreach (var themeName in new[] { "Dark", "Light" })
        {
            var themeDictionary = XDocument.Load(Path.Combine(
                root, "Common", "Frontend", "Assets", "Themes", themeName, "Theme.axaml"));
            Assert.IsTrue(themeDictionary.Descendants().Attributes(xaml + "Key")
                .Any(attribute => attribute.Value == "AppBrush.Border.Subtle"),
                $"{themeName} is missing the TabControl semantic border brush.");
        }

        var profileTabs = XDocument.Load(Path.Combine(
            root, "Common", "Frontend", "Views", "Pages", "Profile", "ProfileTabsView.axaml"));
        var profileTabControl = profileTabs.Descendants(avalonia + "TabControl").Single();
        Assert.AreEqual("{Binding SelectedProfileTabIndex, Mode=TwoWay}",
            (string?)profileTabControl.Attribute("SelectedIndex"));
        var profileItemsPanel = profileTabControl.Element(avalonia + "TabControl.ItemsPanel")!
            .Element(avalonia + "ItemsPanelTemplate")!
            .Element(avalonia + "StackPanel");
        Assert.AreEqual("Horizontal", (string?)profileItemsPanel?.Attribute("Orientation"));
        Assert.AreEqual("Center", (string?)profileItemsPanel?.Attribute("HorizontalAlignment"));

        var singleSpectrumSource = File.ReadAllText(Path.Combine(
            root, "Common", "Frontend", "Views", "Controls", "SingleSpectrumColorView.cs"));
        StringAssert.Contains(singleSpectrumSource, "Find<TabControl>(\"PART_TabControl\")");
        StringAssert.Contains(singleSpectrumSource, "OfType<ItemsPresenter>()");
    }

    [TestMethod]
    public void ComboBoxAndItemsUseApplicationOwnedTemplatesAndPreserveSelectionContracts()
    {
        var root = GetRepositoryRoot();
        var themesPath = Path.Combine(root, "Common", "Frontend", "Assets", "Styles", "ControlThemes.axaml");
        var themes = XDocument.Load(themesPath);
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var comboBoxTheme = themes.Root!.Elements(avalonia + "ControlTheme")
            .Single(theme => (string?)theme.Attribute(xaml + "Key") == "{x:Type ComboBox}");
        Assert.AreEqual("ComboBox", (string?)comboBoxTheme.Attribute("TargetType"));
        Assert.IsTrue(HasSetter(comboBoxTheme, avalonia, "Background", "{DynamicResource AppBrush.Surface.Input}"));
        Assert.IsTrue(HasSetter(comboBoxTheme, avalonia, "BorderBrush", "{DynamicResource AppBrush.Border.Default}"));
        Assert.IsTrue(HasSetter(comboBoxTheme, avalonia, "FocusAdorner", "{x:Null}"));
        Assert.IsTrue(HasSetter(comboBoxTheme, avalonia, "ScrollViewer.HorizontalScrollBarVisibility", "Disabled"));

        var comboBoxTemplate = comboBoxTheme.Descendants(avalonia + "ControlTemplate").Single();
        var popup = comboBoxTemplate.Descendants(avalonia + "Popup").Single();
        Assert.AreEqual("PART_Popup", (string?)popup.Attribute("Name"));
        Assert.AreEqual("{TemplateBinding IsDropDownOpen, Mode=TwoWay}", (string?)popup.Attribute("IsOpen"));
        Assert.AreEqual("Background", (string?)popup.Attribute("PlacementTarget"));
        Assert.AreEqual("True", (string?)popup.Attribute("IsLightDismissEnabled"));

        var itemsPresenter = popup.Descendants(avalonia + "ItemsPresenter").Single();
        Assert.AreEqual("PART_ItemsPresenter", (string?)itemsPresenter.Attribute("Name"));
        Assert.AreEqual("{TemplateBinding ItemsPanel}", (string?)itemsPresenter.Attribute("ItemsPanel"));
        var popupScroller = popup.Descendants(avalonia + "ScrollViewer").Single();
        Assert.AreEqual("False", (string?)popupScroller.Attribute("IsScrollChainingEnabled"));
        Assert.AreEqual("{TemplateBinding ScrollViewer.VerticalScrollBarVisibility}",
            (string?)popupScroller.Attribute("VerticalScrollBarVisibility"));

        var editableTextBox = comboBoxTemplate.Descendants(avalonia + "TextBox")
            .Single(textBox => (string?)textBox.Attribute("Name") == "PART_EditableTextBox");
        Assert.AreEqual("{TemplateBinding Text, Mode=TwoWay}", (string?)editableTextBox.Attribute("Text"));
        Assert.AreEqual("{TemplateBinding IsEditable}", (string?)editableTextBox.Attribute("IsVisible"));
        Assert.IsTrue(comboBoxTemplate.Descendants(avalonia + "ContentControl")
            .Any(control => (string?)control.Attribute("Content") == "{TemplateBinding SelectionBoxItem}" &&
                           (string?)control.Attribute("ContentTemplate") == "{TemplateBinding SelectionBoxItemTemplate}"));
        Assert.IsTrue(comboBoxTemplate.Descendants(avalonia + "Border")
            .Any(border => (string?)border.Attribute("Name") == "PART_FocusRing" ||
                           (string?)border.Attribute(xaml + "Name") == "PART_FocusRing"));

        var comboBoxItemTheme = themes.Root.Elements(avalonia + "ControlTheme")
            .Single(theme => (string?)theme.Attribute(xaml + "Key") == "{x:Type ComboBoxItem}");
        Assert.AreEqual("ComboBoxItem", (string?)comboBoxItemTheme.Attribute("TargetType"));
        var itemTemplate = comboBoxItemTheme.Descendants(avalonia + "ControlTemplate").Single();
        var itemTemplateText = itemTemplate.ToString(SaveOptions.DisableFormatting);
        foreach (var requiredBinding in new[]
                 {
                     "PART_ContentPresenter",
                     "{TemplateBinding Content}",
                     "{TemplateBinding ContentTemplate}",
                     "{TemplateBinding Foreground}",
                     "{TemplateBinding Background}",
                     "{TemplateBinding Padding}",
                     "{TemplateBinding HorizontalContentAlignment}",
                     "{TemplateBinding VerticalContentAlignment}"
                 })
        {
            StringAssert.Contains(itemTemplateText, requiredBinding);
        }

        var itemThemeText = comboBoxItemTheme.ToString(SaveOptions.DisableFormatting);
        foreach (var requiredState in new[] { ":pointerover", ":pressed", ":selected", ":disabled" })
            StringAssert.Contains(itemThemeText, requiredState);

        var resourceKeys = new[]
        {
            "AppBrush.Border.Default",
            "AppBrush.Border.Emphasis",
            "AppBrush.Border.SubtleStrong",
            "AppBrush.Focus",
            "AppBrush.Selection.Background",
            "AppBrush.Selection.Border",
            "AppBrush.Selection.HoverBackground",
            "AppBrush.Selection.HoverBorder",
            "AppBrush.Status.Danger",
            "AppBrush.Surface.Elevated",
            "AppBrush.Surface.Hover",
            "AppBrush.Surface.Input",
            "AppBrush.Surface.Pressed",
            "AppBrush.Surface.Subtle",
            "AppBrush.Surface.SubtleEmphasis",
            "AppBrush.Text.Disabled",
            "AppBrush.Text.Primary",
            "AppBrush.Text.Secondary"
        };
        var themeText = comboBoxTheme.ToString(SaveOptions.DisableFormatting) + itemThemeText;
        foreach (var resource in resourceKeys)
            StringAssert.Contains(themeText, resource);

        foreach (var themeName in new[] { "Dark", "Light" })
        {
            var themeDictionary = XDocument.Load(Path.Combine(
                root, "Common", "Frontend", "Assets", "Themes", themeName, "Theme.axaml"));
            var availableResources = themeDictionary.Descendants()
                .Attributes(xaml + "Key")
                .Select(attribute => attribute.Value)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var resource in resourceKeys)
            {
                Assert.IsTrue(availableResources.Contains(resource),
                    $"{themeName} is missing the ComboBox semantic resource {resource}.");
            }
        }

        var viewRoot = Path.Combine(root, "Common", "Frontend", "Views");
        var comboBoxes = Directory.EnumerateFiles(viewRoot, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(path => XDocument.Load(path).Descendants(avalonia + "ComboBox"))
            .ToArray();
        Assert.AreEqual(4, comboBoxes.Length,
            "The app uses two settings selectors and two password-color selectors; keep their existing bindings in this increment.");
        foreach (var comboBox in comboBoxes)
        {
            Assert.IsNotNull(comboBox.Attribute("ItemsSource"));
            Assert.IsNotNull(comboBox.Attribute("SelectedItem"));
        }
    }

    [TestMethod]
    public void TextBoxUsesAnApplicationOwnedEditingTemplateAndPreservesInputContracts()
    {
        var root = GetRepositoryRoot();
        var themesPath = Path.Combine(root, "Common", "Frontend", "Assets", "Styles", "ControlThemes.axaml");
        var themes = XDocument.Load(themesPath);
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var textBoxTheme = themes.Root!.Elements(avalonia + "ControlTheme")
            .Single(theme => (string?)theme.Attribute(xaml + "Key") == "{x:Type TextBox}");
        Assert.AreEqual("TextBox", (string?)textBoxTheme.Attribute("TargetType"));
        foreach (var property in new[]
                 {
                     "Foreground", "Background", "CaretBrush", "BorderBrush", "SelectionBrush",
                     "SelectionForegroundBrush", "PlaceholderForeground", "BorderThickness", "CornerRadius",
                     "MinHeight", "MinWidth", "Padding", "FocusAdorner",
                     "ScrollViewer.IsScrollChainingEnabled", "ContextFlyout", "Template"
                 })
        {
            Assert.IsTrue(textBoxTheme.Elements(avalonia + "Setter")
                .Any(setter => (string?)setter.Attribute("Property") == property),
                $"The TextBox theme must define {property}.");
        }

        var template = textBoxTheme.Descendants(avalonia + "ControlTemplate").Single();
        var templateText = template.ToString(SaveOptions.DisableFormatting);
        foreach (var requiredPart in new[]
                 {
                     "PART_BorderElement",
                     "PART_InnerDockPanel",
                     "PART_FloatingPlaceholder",
                     "PART_ScrollViewer",
                     "PART_Placeholder",
                     "PART_TextPresenter",
                     "{TemplateBinding InnerLeftContent}",
                     "{TemplateBinding InnerRightContent}",
                     "{TemplateBinding Text, Mode=TwoWay}",
                     "{TemplateBinding CaretIndex}",
                     "{TemplateBinding SelectionStart}",
                     "{TemplateBinding SelectionEnd}",
                     "{TemplateBinding PasswordChar}",
                     "{TemplateBinding RevealPassword}",
                     "{TemplateBinding SelectionBrush}",
                     "{TemplateBinding SelectionForegroundBrush}",
                     "{TemplateBinding (ScrollViewer.HorizontalScrollBarVisibility)}",
                     "{TemplateBinding (ScrollViewer.VerticalScrollBarVisibility)}",
                     "BringIntoViewOnFocusChange",
                     "PreeditText"
                 })
        {
            StringAssert.Contains(templateText, requiredPart);
        }

        var themeText = textBoxTheme.ToString(SaveOptions.DisableFormatting);
        foreach (var requiredState in new[] { ":touch-mode", ":disabled", ":pointerover", ":focus", ":error", "UseFloatingPlaceholder", "registrationInvalid" })
            StringAssert.Contains(themeText, requiredState);

        var defaultContextFlyout = themes.Root.Elements(avalonia + "MenuFlyout")
            .Single(flyout => (string?)flyout.Attribute(xaml + "Key") == "AppTextBoxContextFlyout");
        var mobileContextFlyout = themes.Root.Elements(avalonia + "MenuFlyout")
            .Single(flyout => (string?)flyout.Attribute(xaml + "Key") == "AppTextBoxContextFlyoutMobile");
        Assert.AreEqual(3, defaultContextFlyout.Elements(avalonia + "MenuItem").Count());
        Assert.AreEqual(3, mobileContextFlyout.Elements(avalonia + "MenuItem").Count());
        foreach (var flyout in new[] { defaultContextFlyout, mobileContextFlyout })
        {
            foreach (var item in flyout.Elements(avalonia + "MenuItem"))
            {
                Assert.IsNotNull(item.Attribute("Header"));
                Assert.IsNotNull(item.Attribute("Command"));
                Assert.IsNotNull(item.Attribute("IsEnabled"));
            }
        }
        Assert.IsTrue((string?)mobileContextFlyout.Attribute("FlyoutPresenterTheme")
            == "{DynamicResource AppHorizontalTextMenuFlyoutPresenter}");
        Assert.IsTrue((string?)mobileContextFlyout.Attribute("ItemContainerTheme")
            == "{DynamicResource AppHorizontalTextMenuItem}");

        var resourceKeys = new[]
        {
            "AppBrush.Border.Default",
            "AppBrush.Border.Emphasis",
            "AppBrush.Border.SubtleStrong",
            "AppBrush.Focus",
            "AppBrush.Selection.Background",
            "AppBrush.Status.Danger",
            "AppBrush.Surface.Input",
            "AppBrush.Surface.Subtle",
            "AppBrush.Surface.SubtleEmphasis",
            "AppBrush.Text.Disabled",
            "AppBrush.Text.Primary",
            "AppBrush.Text.Secondary"
        };
        foreach (var themeName in new[] { "Dark", "Light" })
        {
            var themeDictionary = XDocument.Load(Path.Combine(
                root, "Common", "Frontend", "Assets", "Themes", themeName, "Theme.axaml"));
            var availableResources = themeDictionary.Descendants()
                .Attributes(xaml + "Key")
                .Select(attribute => attribute.Value)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var resource in resourceKeys)
            {
                Assert.IsTrue(availableResources.Contains(resource),
                    $"{themeName} is missing the TextBox semantic resource {resource}.");
            }
        }

        var viewRoot = Path.Combine(root, "Common", "Frontend", "Views");
        var textBoxes = Directory.EnumerateFiles(viewRoot, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(path => XDocument.Load(path).Descendants(avalonia + "TextBox"))
            .ToArray();
        Assert.AreEqual(28, textBoxes.Length,
            "The app's existing editable and password TextBoxes are the scope of this template increment.");
        Assert.AreEqual(9, textBoxes.Count(textBox => textBox.Attribute("PasswordChar") is not null),
            "Keep the password-masking usages connected to TextPresenter.PasswordChar.");

        var watermarkedTextBoxSource = File.ReadAllText(Path.Combine(
            root, "Common", "Frontend", "Views", "Controls", "WatermarkWidthTextBox.cs"));
        StringAssert.Contains(watermarkedTextBoxSource, "StyleKeyOverride => typeof(TextBox)");
        var watermarkedControls = Directory.EnumerateFiles(viewRoot, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(path => XDocument.Load(path).Descendants())
            .Count(element => element.Name.LocalName == "WatermarkWidthTextBox");
        Assert.AreEqual(4, watermarkedControls);

        var registrationView = XDocument.Load(Path.Combine(
            viewRoot, "Auth", "RegistrationView.axaml"));
        var invalidStyle = registrationView.Descendants(avalonia + "Style")
            .Single(style => (string?)style.Attribute("Selector") == "TextBox.registrationInvalid");
        Assert.IsTrue(HasSetter(invalidStyle, avalonia, "BorderBrush", "{DynamicResource AppBrush.Status.Danger}"));
    }

    [TestMethod]
    public void MenuFlyoutPresenterUsesAnApplicationOwnedThemeAndRetainsKeyboardAndScrollContracts()
    {
        var root = GetRepositoryRoot();
        var themes = XDocument.Load(Path.Combine(
            root, "Common", "Frontend", "Assets", "Styles", "ControlThemes.axaml"));
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var presenterTheme = themes.Root!.Elements(avalonia + "ControlTheme")
            .Single(theme => (string?)theme.Attribute(xaml + "Key") == "{x:Type MenuFlyoutPresenter}");

        Assert.AreEqual("MenuFlyoutPresenter", (string?)presenterTheme.Attribute("TargetType"));
        var requiredResources = new[] { "AppBrush.Border.Default", "AppBrush.Surface.Elevated" };
        foreach (var resource in requiredResources)
            StringAssert.Contains(presenterTheme.ToString(SaveOptions.DisableFormatting), resource);

        var template = presenterTheme.Descendants(avalonia + "ControlTemplate").Single();
        var rootBorder = template.Descendants(avalonia + "Border")
            .Single(border => (string?)border.Attribute("Name") == "LayoutRoot");
        Assert.AreEqual("{TemplateBinding Background}", (string?)rootBorder.Attribute("Background"));
        Assert.AreEqual("{TemplateBinding BorderBrush}", (string?)rootBorder.Attribute("BorderBrush"));
        Assert.AreEqual("{TemplateBinding BorderThickness}", (string?)rootBorder.Attribute("BorderThickness"));
        Assert.AreEqual("{TemplateBinding CornerRadius}", (string?)rootBorder.Attribute("CornerRadius"));

        var scrollViewer = template.Descendants(avalonia + "ScrollViewer").Single();
        Assert.AreEqual("{TemplateBinding ScrollViewer.HorizontalScrollBarVisibility}",
            (string?)scrollViewer.Attribute("HorizontalScrollBarVisibility"));
        Assert.AreEqual("{TemplateBinding ScrollViewer.VerticalScrollBarVisibility}",
            (string?)scrollViewer.Attribute("VerticalScrollBarVisibility"));
        Assert.IsNull(scrollViewer.Attribute("Theme"),
            "The menu scroll viewer should use the currently installed default ScrollViewer theme.");

        var itemsPresenter = template.Descendants(avalonia + "ItemsPresenter")
            .Single(presenter => (string?)presenter.Attribute("Name") == "PART_ItemsPresenter" ||
                                 (string?)presenter.Attribute(xaml + "Name") == "PART_ItemsPresenter");
        Assert.AreEqual("{TemplateBinding ItemsPanel}", (string?)itemsPresenter.Attribute("ItemsPanel"));
        Assert.AreEqual("0,4", (string?)itemsPresenter.Attribute("Margin"));
        Assert.AreEqual("Continue", (string?)itemsPresenter.Attribute("KeyboardNavigation.TabNavigation"));
        Assert.AreEqual("True", (string?)itemsPresenter.Attribute("Grid.IsSharedSizeScope"));

        foreach (var themeName in new[] { "Dark", "Light" })
        {
            var themeDictionary = XDocument.Load(Path.Combine(
                root, "Common", "Frontend", "Assets", "Themes", themeName, "Theme.axaml"));
            var resourceKeys = themeDictionary.Descendants()
                .Attributes(xaml + "Key")
                .Select(attribute => attribute.Value)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var resource in requiredResources)
                Assert.IsTrue(resourceKeys.Contains(resource), $"{themeName} is missing {resource}.");
        }

        var viewRoot = Path.Combine(root, "Common", "Frontend", "Views");
        var menuFlyouts = Directory.EnumerateFiles(viewRoot, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(path => XDocument.Load(path).Descendants(avalonia + "MenuFlyout"))
            .ToArray();
        Assert.IsTrue(menuFlyouts.Length >= 5,
            "The app's existing page and toolbar menus should continue using MenuFlyout.");

        var mobileTextFlyout = themes.Root.Elements(avalonia + "MenuFlyout")
            .Single(flyout => (string?)flyout.Attribute(xaml + "Key") == "AppTextBoxContextFlyoutMobile");
        Assert.AreEqual("{DynamicResource AppHorizontalTextMenuFlyoutPresenter}",
            (string?)mobileTextFlyout.Attribute("FlyoutPresenterTheme"));
        Assert.AreEqual("{DynamicResource AppHorizontalTextMenuItem}",
            (string?)mobileTextFlyout.Attribute("ItemContainerTheme"));
    }

    [TestMethod]
    public void MenuItemAndSeparatorUseApplicationOwnedThemesAndPreserveMenuBehavior()
    {
        var root = GetRepositoryRoot();
        var themesPath = Path.Combine(root, "Common", "Frontend", "Assets", "Styles", "ControlThemes.axaml");
        var themes = XDocument.Load(themesPath);
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var menuItemTheme = themes.Root!.Elements(avalonia + "ControlTheme")
            .Single(theme => (string?)theme.Attribute(xaml + "Key") == "{x:Type MenuItem}");
        Assert.AreEqual("MenuItem", (string?)menuItemTheme.Attribute("TargetType"));
        var menuItemText = menuItemTheme.ToString(SaveOptions.DisableFormatting);
        foreach (var requiredContract in new[]
                 {
                     "PART_LayoutRoot",
                     "PART_ToggleIconPresenter",
                     "PART_IconPresenter",
                     "PART_HeaderPresenter",
                     "{TemplateBinding Header}",
                     "{TemplateBinding HeaderTemplate}",
                     "PART_InputGestureText",
                     "{TemplateBinding InputGesture, Converter={StaticResource AppMenuKeyGestureConverter}}",
                     "PART_ChevronPath",
                     "PART_Popup",
                     "{TemplateBinding IsSubMenuOpen, Mode=TwoWay}",
                     "PART_ItemsPresenter",
                     "Placement=\"RightEdgeAlignedTop\"",
                     "IsLightDismissEnabled=\"False\"",
                     ":selected",
                     ":pressed",
                     ":disabled",
                     ":open",
                     ":empty",
                     ":checked:toggle",
                     ":checked:radio"
                 })
        {
            StringAssert.Contains(menuItemText, requiredContract);
        }
        var headerAccessKeyStyle = menuItemTheme.Elements(avalonia + "Style")
            .Single(style => (string?)style.Attribute("Selector") ==
                             "^ /template/ ContentPresenter#PART_HeaderPresenter");
        Assert.IsTrue(HasSetter(headerAccessKeyStyle, avalonia, "RecognizesAccessKey", "True"),
            "MenuItem headers must continue recognizing access keys.");
        var separatorTheme = themes.Root.Elements(avalonia + "ControlTheme")
            .Single(theme => (string?)theme.Attribute(xaml + "Key") == "{x:Type Separator}");
        Assert.AreEqual("Separator", (string?)separatorTheme.Attribute("TargetType"));
        Assert.IsTrue(HasSetter(separatorTheme, avalonia,
            "Background", "{DynamicResource AppBrush.Border.Subtle}"));
        StringAssert.Contains(separatorTheme.ToString(SaveOptions.DisableFormatting), "{TemplateBinding Background}");

        var horizontalPresenterTheme = themes.Root.Elements(avalonia + "ControlTheme")
            .Single(theme => (string?)theme.Attribute(xaml + "Key") == "AppHorizontalTextMenuFlyoutPresenter");
        Assert.AreEqual("MenuFlyoutPresenter", (string?)horizontalPresenterTheme.Attribute("TargetType"));
        Assert.AreEqual("{StaticResource {x:Type MenuFlyoutPresenter}}",
            (string?)horizontalPresenterTheme.Attribute("BasedOn"));
        var horizontalItemsPanel = horizontalPresenterTheme.Descendants(avalonia + "StackPanel").Single();
        Assert.AreEqual("Horizontal", (string?)horizontalItemsPanel.Attribute("Orientation"));

        var horizontalItemTheme = themes.Root.Elements(avalonia + "ControlTheme")
            .Single(theme => (string?)theme.Attribute(xaml + "Key") == "AppHorizontalTextMenuItem");
        Assert.AreEqual("MenuItem", (string?)horizontalItemTheme.Attribute("TargetType"));
        Assert.AreEqual("{StaticResource {x:Type MenuItem}}",
            (string?)horizontalItemTheme.Attribute("BasedOn"));

        var viewRoot = Path.Combine(root, "Common", "Frontend", "Views");
        var viewFiles = Directory.EnumerateFiles(viewRoot, "*.axaml", SearchOption.AllDirectories).ToArray();
        var viewMenuItems = viewFiles.SelectMany(path => XDocument.Load(path).Descendants(avalonia + "MenuItem")).ToArray();
        var viewSeparators = viewFiles.SelectMany(path => XDocument.Load(path).Descendants(avalonia + "Separator")).ToArray();
        Assert.AreEqual(21, viewMenuItems.Length,
            "All current view MenuItems should receive the app-owned default theme.");
        Assert.AreEqual(2, viewSeparators.Length,
            "Both view separators should receive the app-owned default theme.");

        var textFlyouts = themes.Root.Elements(avalonia + "MenuFlyout")
            .Where(flyout => new[] { "AppTextBoxContextFlyout", "AppTextBoxContextFlyoutMobile" }
                .Contains((string?)flyout.Attribute(xaml + "Key"), StringComparer.Ordinal))
            .ToArray();
        Assert.AreEqual(6, textFlyouts.Sum(flyout => flyout.Elements(avalonia + "MenuItem").Count()));
        var scrollBarFlyouts = themes.Root.Elements(avalonia + "MenuFlyout")
            .Where(flyout => new[]
                {
                    "AppVerticalScrollBarContextFlyout",
                    "AppHorizontalScrollBarContextFlyout"
                }.Contains((string?)flyout.Attribute(xaml + "Key"), StringComparer.Ordinal))
            .ToArray();
        Assert.AreEqual(14, scrollBarFlyouts.Sum(flyout => flyout.Elements(avalonia + "MenuItem").Count()));
        Assert.AreEqual(6, scrollBarFlyouts.Sum(flyout => flyout.Elements(avalonia + "Separator").Count()));

        foreach (var themeName in new[] { "Dark", "Light" })
        {
            var themeDictionary = XDocument.Load(Path.Combine(
                root, "Common", "Frontend", "Assets", "Themes", themeName, "Theme.axaml"));
            var resourceKeys = themeDictionary.Descendants()
                .Attributes(xaml + "Key")
                .Select(attribute => attribute.Value)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var resource in new[]
                     {
                         "AppBrush.Border.Default",
                         "AppBrush.Border.Subtle",
                         "AppBrush.Surface.Elevated",
                         "AppBrush.Surface.Hover",
                         "AppBrush.Surface.Pressed",
                         "AppBrush.Text.Disabled",
                         "AppBrush.Text.Primary",
                         "AppBrush.Text.Secondary"
                     })
            {
                Assert.IsTrue(resourceKeys.Contains(resource),
                    $"{themeName} is missing the menu theme resource {resource}.");
            }
        }
    }

    [TestMethod]
    public void FlyoutPresenterUsesAnApplicationOwnedThemeAndPreservesContentLayout()
    {
        var root = GetRepositoryRoot();
        var themes = XDocument.Load(Path.Combine(
            root, "Common", "Frontend", "Assets", "Styles", "ControlThemes.axaml"));
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var presenterTheme = themes.Root!.Elements(avalonia + "ControlTheme")
            .Single(theme => (string?)theme.Attribute(xaml + "Key") == "{x:Type FlyoutPresenter}");

        Assert.AreEqual("FlyoutPresenter", (string?)presenterTheme.Attribute("TargetType"));
        foreach (var (property, value) in new[]
                 {
                     ("Background", "{DynamicResource AppBrush.Surface.Elevated}"),
                     ("Foreground", "{DynamicResource AppBrush.Text.Primary}"),
                     ("BorderBrush", "{DynamicResource AppBrush.Border.Default}"),
                     ("BorderThickness", "1"),
                     ("CornerRadius", "8"),
                     ("Padding", "12,11,12,12"),
                     ("MinWidth", "96"),
                     ("MinHeight", "40"),
                     ("MaxWidth", "456"),
                     ("MaxHeight", "758")
                 })
        {
            Assert.IsTrue(HasSetter(presenterTheme, avalonia, property, value),
                $"FlyoutPresenter must retain its {property} contract.");
        }

        var template = presenterTheme.Descendants(avalonia + "ControlTemplate").Single();
        var rootBorder = template.Descendants(avalonia + "Border")
            .Single(border => (string?)border.Attribute("Name") == "LayoutRoot");
        Assert.AreEqual("{TemplateBinding Background}", (string?)rootBorder.Attribute("Background"));
        Assert.AreEqual("{TemplateBinding BorderBrush}", (string?)rootBorder.Attribute("BorderBrush"));
        Assert.AreEqual("{TemplateBinding BorderThickness}", (string?)rootBorder.Attribute("BorderThickness"));
        Assert.AreEqual("0", (string?)rootBorder.Attribute("Padding"));
        Assert.AreEqual("{TemplateBinding CornerRadius}", (string?)rootBorder.Attribute("CornerRadius"));

        var contentPresenter = template.Descendants(avalonia + "ContentPresenter")
            .Single(presenter => (string?)presenter.Attribute("Name") == "PART_ContentPresenter" ||
                                 (string?)presenter.Attribute(xaml + "Name") == "PART_ContentPresenter");
        Assert.AreEqual("{TemplateBinding Content}", (string?)contentPresenter.Attribute("Content"));
        Assert.AreEqual("{TemplateBinding ContentTemplate}", (string?)contentPresenter.Attribute("ContentTemplate"));
        Assert.AreEqual("{TemplateBinding Foreground}", (string?)contentPresenter.Attribute("Foreground"));
        Assert.AreEqual("{TemplateBinding Padding}", (string?)contentPresenter.Attribute("Padding"));
        Assert.AreEqual("{TemplateBinding HorizontalContentAlignment}",
            (string?)contentPresenter.Attribute("HorizontalContentAlignment"));
        Assert.AreEqual("{TemplateBinding VerticalContentAlignment}",
            (string?)contentPresenter.Attribute("VerticalContentAlignment"));
        Assert.AreEqual("True", (string?)contentPresenter.Attribute("RecognizesAccessKey"));

        foreach (var themeName in new[] { "Dark", "Light" })
        {
            var themeDictionary = XDocument.Load(Path.Combine(
                root, "Common", "Frontend", "Assets", "Themes", themeName, "Theme.axaml"));
            var resourceKeys = themeDictionary.Descendants()
                .Attributes(xaml + "Key")
                .Select(attribute => attribute.Value)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var resource in new[]
                     {
                         "AppBrush.Surface.Elevated",
                         "AppBrush.Text.Primary",
                         "AppBrush.Border.Default"
                     })
            {
                Assert.IsTrue(resourceKeys.Contains(resource), $"{themeName} is missing {resource}.");
            }
        }

        var viewRoot = Path.Combine(root, "Common", "Frontend", "Views");
        var flyouts = Directory.EnumerateFiles(viewRoot, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(path => XDocument.Load(path).Descendants(avalonia + "Flyout"))
            .ToArray();
        Assert.AreEqual(6, flyouts.Length,
            "The application-owned default presenter should cover all existing view Flyouts.");
        var codeFlyoutSource = File.ReadAllText(Path.Combine(
            viewRoot, "Controls", "PasswordStrengthInfoButton.axaml.cs"));
        StringAssert.Contains(codeFlyoutSource, "new Flyout");
    }

    [TestMethod]
    public void ScrollViewerUsesAnApplicationOwnedTemplateAndPreservesScrollingContracts()
    {
        var root = GetRepositoryRoot();
        var themes = XDocument.Load(Path.Combine(
            root, "Common", "Frontend", "Assets", "Styles", "ControlThemes.axaml"));
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var scrollViewerTheme = themes.Root!.Elements(avalonia + "ControlTheme")
            .Single(theme => (string?)theme.Attribute(xaml + "Key") == "{x:Type ScrollViewer}");

        Assert.AreEqual("ScrollViewer", (string?)scrollViewerTheme.Attribute("TargetType"));
        Assert.IsTrue(HasSetter(scrollViewerTheme, avalonia, "Background", "Transparent"));

        var template = scrollViewerTheme.Descendants(avalonia + "ControlTemplate").Single();
        var contentPresenter = template.Descendants(avalonia + "ScrollContentPresenter")
            .Single(presenter => (string?)presenter.Attribute("Name") == "PART_ContentPresenter");
        foreach (var (property, value) in new[]
                 {
                     ("Background", "{TemplateBinding Background}"),
                     ("HorizontalSnapPointsType", "{TemplateBinding HorizontalSnapPointsType}"),
                     ("VerticalSnapPointsType", "{TemplateBinding VerticalSnapPointsType}"),
                     ("HorizontalSnapPointsAlignment", "{TemplateBinding HorizontalSnapPointsAlignment}"),
                     ("VerticalSnapPointsAlignment", "{TemplateBinding VerticalSnapPointsAlignment}"),
                     ("Padding", "{TemplateBinding Padding}"),
                     ("ScrollViewer.IsScrollInertiaEnabled", "{TemplateBinding IsScrollInertiaEnabled}")
                 })
        {
            Assert.AreEqual(value, (string?)contentPresenter.Attribute(property),
                $"ScrollViewer must forward {property} to its content presenter.");
        }

        var gesture = contentPresenter.Descendants(avalonia + "ScrollGestureRecognizer").Single();
        foreach (var (property, value) in new[]
                 {
                     ("CanHorizontallyScroll", "{Binding CanHorizontallyScroll, ElementName=PART_ContentPresenter}"),
                     ("CanVerticallyScroll", "{Binding CanVerticallyScroll, ElementName=PART_ContentPresenter}"),
                     ("IsScrollInertiaEnabled", "{Binding (ScrollViewer.IsScrollInertiaEnabled), ElementName=PART_ContentPresenter}"),
                     ("Offset", "{Binding Offset, ElementName=PART_ContentPresenter}"),
                     ("Viewport", "{Binding Viewport, ElementName=PART_ContentPresenter}"),
                     ("Extent", "{Binding Extent, ElementName=PART_ContentPresenter}")
                 })
        {
            Assert.AreEqual(value, (string?)gesture.Attribute(property),
                $"The scroll gesture must keep its {property} binding.");
        }

        var horizontalScrollBar = template.Descendants(avalonia + "ScrollBar")
            .Single(bar => (string?)bar.Attribute("Name") == "PART_HorizontalScrollBar");
        Assert.AreEqual("Horizontal", (string?)horizontalScrollBar.Attribute("Orientation"));
        Assert.IsNull(horizontalScrollBar.Attribute("Theme"),
            "ScrollBar should inherit the application-owned default theme without an instance override.");
        var verticalScrollBar = template.Descendants(avalonia + "ScrollBar")
            .Single(bar => (string?)bar.Attribute("Name") == "PART_VerticalScrollBar");
        Assert.AreEqual("Vertical", (string?)verticalScrollBar.Attribute("Orientation"));
        Assert.IsNull(verticalScrollBar.Attribute("Theme"),
            "ScrollBar should inherit the application-owned default theme without an instance override.");

        var separator = template.Descendants(avalonia + "Panel")
            .Single(panel => (string?)panel.Attribute("Name") == "PART_ScrollBarsSeparator");
        Assert.AreEqual("{DynamicResource AppBrush.Scrollbar.Track}",
            (string?)separator.Attribute("Background"));
        Assert.IsTrue(scrollViewerTheme.Elements(avalonia + "Style").Any(style =>
            (string?)style.Attribute("Selector") == "^[IsExpanded=true] /template/ Panel#PART_ScrollBarsSeparator" &&
            HasSetter(style, avalonia, "Opacity", "1")));
        var autoHideStyle = scrollViewerTheme.Elements(avalonia + "Style").Single(style =>
            (string?)style.Attribute("Selector") ==
            "^[AllowAutoHide=True] /template/ ScrollContentPresenter#PART_ContentPresenter");
        Assert.IsTrue(HasSetter(autoHideStyle, avalonia, "Grid.ColumnSpan", "2"));
        Assert.IsTrue(HasSetter(autoHideStyle, avalonia, "Grid.RowSpan", "2"));

        var viewRoot = Path.Combine(root, "Common", "Frontend", "Views");
        var scrollViewers = Directory.EnumerateFiles(viewRoot, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(path => XDocument.Load(path).Descendants(avalonia + "ScrollViewer"))
            .ToArray();
        Assert.AreEqual(28, scrollViewers.Length,
            "The application-owned default theme should cover all existing view ScrollViewers.");
        var codeScrollerSource = File.ReadAllText(Path.Combine(
            viewRoot, "Controls", "PasswordStrengthInfoButton.axaml.cs"));
        StringAssert.Contains(codeScrollerSource, "new ScrollViewer");

        var commonStyles = XDocument.Load(Path.Combine(
            root, "Common", "Frontend", "Assets", "Styles", "CommonStyles.axaml"));
        Assert.IsTrue(commonStyles.Descendants(avalonia + "Style").Any(style =>
            (string?)style.Attribute("Selector") ==
            "ScrollViewer.comfortableListScroller /template/ ScrollBar#PART_VerticalScrollBar:vertical"));
        Assert.IsTrue(commonStyles.Descendants(avalonia + "Style").Any(style =>
            (string?)style.Attribute("Selector") ==
            "ScrollViewer.comfortableListScroller /template/ ScrollBar#PART_VerticalScrollBar:vertical /template/ Thumb"));

        foreach (var themeName in new[] { "Dark", "Light" })
        {
            var themeDictionary = XDocument.Load(Path.Combine(
                root, "Common", "Frontend", "Assets", "Themes", themeName, "Theme.axaml"));
            var resourceKeys = themeDictionary.Descendants()
                .Attributes(xaml + "Key")
                .Select(attribute => attribute.Value)
                .ToHashSet(StringComparer.Ordinal);
            Assert.IsTrue(resourceKeys.Contains("AppBrush.Scrollbar.Track"),
                $"{themeName} is missing AppBrush.Scrollbar.Track.");
        }
    }

    [TestMethod]
    public void ScrollBarUsesApplicationOwnedOrientationTemplatesAndKeepsRequiredPartContracts()
    {
        var root = GetRepositoryRoot();
        var themes = XDocument.Load(Path.Combine(
            root, "Common", "Frontend", "Assets", "Styles", "ControlThemes.axaml"));
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var scrollBarTheme = themes.Root!.Elements(avalonia + "ControlTheme")
            .Single(theme => (string?)theme.Attribute(xaml + "Key") == "{x:Type ScrollBar}");
        Assert.AreEqual("ScrollBar", (string?)scrollBarTheme.Attribute("TargetType"));
        Assert.IsTrue(HasSetter(scrollBarTheme, avalonia, "MinWidth", "12"));
        Assert.IsTrue(HasSetter(scrollBarTheme, avalonia, "MinHeight", "12"));
        Assert.IsTrue(HasSetter(scrollBarTheme, avalonia, "Background", "Transparent"));

        foreach (var (selector, orientation, reversed, pageUpName, pageDownName) in new[]
                 {
                     ("^:vertical", "Vertical", "True", "Page up", "Page down"),
                     ("^:horizontal", "Horizontal", "False", "Page left", "Page right")
                 })
        {
            var orientationStyle = scrollBarTheme.Elements(avalonia + "Style")
                .Single(style => (string?)style.Attribute("Selector") == selector);
            var template = orientationStyle.Descendants(avalonia + "ControlTemplate").Single();
            var track = template.Descendants(avalonia + "Track").Single();
            Assert.AreEqual("PART_Track", (string?)track.Attribute("Name"));
            Assert.AreEqual(orientation, (string?)track.Attribute("Orientation"));
            Assert.AreEqual(reversed, (string?)track.Attribute("IsDirectionReversed"));
            Assert.AreEqual("{TemplateBinding Minimum}", (string?)track.Attribute("Minimum"));
            Assert.AreEqual("{TemplateBinding Maximum}", (string?)track.Attribute("Maximum"));
            Assert.AreEqual("{TemplateBinding Value, Mode=TwoWay}", (string?)track.Attribute("Value"));
            Assert.AreEqual("{TemplateBinding ViewportSize}", (string?)track.Attribute("ViewportSize"));
            Assert.AreEqual("{TemplateBinding ScrollViewer.IsDeferredScrollingEnabled}",
                (string?)track.Attribute("DeferThumbDrag"));

            var expectedParts = new[]
            {
                "PART_LineUpButton", "PART_LineDownButton", "PART_PageUpButton", "PART_PageDownButton",
                "TrackRect"
            };
            foreach (var partName in expectedParts)
            {
                Assert.AreEqual(1, template.Descendants()
                    .Count(element => (string?)element.Attribute("Name") == partName),
                    $"The {orientation} ScrollBar template must retain {partName}.");
            }

            foreach (var buttonName in new[] { "PART_LineUpButton", "PART_LineDownButton" })
            {
                var lineButton = template.Descendants(avalonia + "RepeatButton")
                    .Single(button => (string?)button.Attribute("Name") == buttonName);
                Assert.AreEqual("{StaticResource AppScrollBarLineButtonTheme}",
                    (string?)lineButton.Attribute("Theme"));
            }
            foreach (var buttonName in new[] { "PART_PageUpButton", "PART_PageDownButton" })
            {
                var pageButton = track.Descendants(avalonia + "RepeatButton")
                    .Single(button => (string?)button.Attribute("Name") == buttonName);
                Assert.AreEqual("{StaticResource AppScrollBarPageButtonTheme}",
                    (string?)pageButton.Attribute("Theme"));
            }

            Assert.AreEqual(pageUpName, (string?)track.Descendants(avalonia + "RepeatButton")
                .Single(button => (string?)button.Attribute("Name") == "PART_PageUpButton")
                .Attribute("AutomationProperties.Name"));
            Assert.AreEqual(pageDownName, (string?)track.Descendants(avalonia + "RepeatButton")
                .Single(button => (string?)button.Attribute("Name") == "PART_PageDownButton")
                .Attribute("AutomationProperties.Name"));

            var trackRect = template.Descendants(avalonia + "Rectangle")
                .Single(rectangle => (string?)rectangle.Attribute("Name") == "TrackRect");
            Assert.AreEqual("{DynamicResource AppBrush.Scrollbar.Track}",
                (string?)trackRect.Attribute("Fill"));
            Assert.IsTrue(track.Descendants(avalonia + "Thumb")
                .Any(thumb => (string?)thumb.Attribute("Theme") == "{StaticResource AppScrollBarThumbTheme}"));
        }

        foreach (var (resourceKey, targetType) in new[]
                 {
                     ("AppScrollBarThumbTheme", "Thumb"),
                     ("AppScrollBarPageButtonTheme", "RepeatButton"),
                     ("AppScrollBarLineButtonTheme", "RepeatButton")
                 })
        {
            var subTheme = themes.Root.Elements(avalonia + "ControlTheme")
                .Single(theme => (string?)theme.Attribute(xaml + "Key") == resourceKey);
            Assert.AreEqual(targetType, (string?)subTheme.Attribute("TargetType"));
        }

        var thumbTheme = themes.Root.Elements(avalonia + "ControlTheme")
            .Single(theme => (string?)theme.Attribute(xaml + "Key") == "AppScrollBarThumbTheme");
        Assert.IsTrue(HasSetter(thumbTheme, avalonia, "Background",
            "{DynamicResource AppBrush.Scrollbar.Thumb}"));
        Assert.IsTrue(thumbTheme.Elements(avalonia + "Style").Any(style =>
            (string?)style.Attribute("Selector") == "^:pointerover /template/ Border" &&
            HasSetter(style, avalonia, "Background", "{DynamicResource AppBrush.Scrollbar.ThumbHover}")));
        Assert.IsTrue(thumbTheme.Elements(avalonia + "Style").Any(style =>
            (string?)style.Attribute("Selector") == "^:pressed /template/ Border" &&
            HasSetter(style, avalonia, "Background", "{DynamicResource AppBrush.Scrollbar.ThumbPressed}")));

        Assert.IsTrue(scrollBarTheme.Elements(avalonia + "Style").Any(style =>
            (string?)style.Attribute("Selector") == "^[IsExpanded=true]" &&
            style.Descendants(avalonia + "Setter").Any(setter =>
                (string?)setter.Attribute("Property") == "Opacity" &&
                (string?)setter.Attribute("Value") == "1")));

        var verticalFlyout = themes.Root.Elements(avalonia + "MenuFlyout")
            .Single(flyout => (string?)flyout.Attribute(xaml + "Key") == "AppVerticalScrollBarContextFlyout");
        Assert.IsTrue(verticalFlyout.Descendants(avalonia + "MenuItem")
            .Any(item => (string?)item.Attribute("Command") == "{Binding $parent[ScrollBar].ScrollToHome}"));
        Assert.IsTrue(verticalFlyout.Descendants(avalonia + "MenuItem")
            .Any(item => (string?)item.Attribute("Command") == "{Binding $parent[ScrollBar].PageDown}"));
        var horizontalFlyout = themes.Root.Elements(avalonia + "MenuFlyout")
            .Single(flyout => (string?)flyout.Attribute(xaml + "Key") == "AppHorizontalScrollBarContextFlyout");
        Assert.IsTrue(horizontalFlyout.Descendants(avalonia + "MenuItem")
            .Any(item => (string?)item.Attribute("Command") == "{Binding $parent[ScrollBar].LineLeft}"));
        Assert.IsTrue(horizontalFlyout.Descendants(avalonia + "MenuItem")
            .Any(item => (string?)item.Attribute("Command") == "{Binding $parent[ScrollBar].LineRight}"));

        var expectedVerticalItems = new[]
        {
            ("ScrollBarScrollHereLabel", "ScrollHere", "ScrollHere"),
            ("ScrollBarTopLabel", "ScrollToHome", "Top"),
            ("ScrollBarBottomLabel", "ScrollToEnd", "Bottom"),
            ("ScrollBarPageUpLabel", "PageUp", "PageUp"),
            ("ScrollBarPageDownLabel", "PageDown", "PageDown"),
            ("ScrollBarScrollUpLabel", "LineUp", "ScrollUp"),
            ("ScrollBarScrollDownLabel", "LineDown", "ScrollDown")
        };
        var expectedHorizontalItems = new[]
        {
            ("ScrollBarScrollHereLabel", "ScrollHere", "ScrollHere"),
            ("ScrollBarLeftEdgeLabel", "ScrollToHome", "LeftEdge"),
            ("ScrollBarRightEdgeLabel", "ScrollToEnd", "RightEdge"),
            ("ScrollBarPageLeftLabel", "PageLeft", "PageLeft"),
            ("ScrollBarPageRightLabel", "PageRight", "PageRight"),
            ("ScrollBarScrollLeftLabel", "LineLeft", "ScrollLeft"),
            ("ScrollBarScrollRightLabel", "LineRight", "ScrollRight")
        };
        foreach (var (flyout, expectedItems) in new[]
                 {
                     (verticalFlyout, expectedVerticalItems),
                     (horizontalFlyout, expectedHorizontalItems)
                 })
        {
            var menuItems = flyout.Elements(avalonia + "MenuItem").ToArray();
            Assert.AreEqual(7, menuItems.Length);
            for (var index = 0; index < expectedItems.Length; index++)
            {
                var (labelProperty, command, automationId) = expectedItems[index];
                Assert.AreEqual(
                    $"{{ReflectionBinding $parent[ScrollBar].DataContext.{labelProperty}}}",
                    (string?)menuItems[index].Attribute("Header"));
                Assert.AreEqual(
                    $"{{Binding $parent[ScrollBar].{command}}}",
                    (string?)menuItems[index].Attribute("Command"));
                Assert.AreEqual(automationId,
                    (string?)menuItems[index].Attribute("AutomationProperties.AutomationId"));
            }
            Assert.AreEqual(3, flyout.Elements(avalonia + "Separator").Count());
        }

        var expectedLocalizationKeys = new[]
        {
            "ScrollBar_Context_ScrollHere", "ScrollBar_Context_Top", "ScrollBar_Context_Bottom",
            "ScrollBar_Context_PageUp", "ScrollBar_Context_PageDown", "ScrollBar_Context_ScrollUp",
            "ScrollBar_Context_ScrollDown", "ScrollBar_Context_LeftEdge", "ScrollBar_Context_RightEdge",
            "ScrollBar_Context_PageLeft", "ScrollBar_Context_PageRight", "ScrollBar_Context_ScrollLeft",
            "ScrollBar_Context_ScrollRight"
        };
        var viewModelBaseType = typeof(PasswordManagerLocal.Common.Frontend.ViewModels.ViewModelBase);
        var viewModelBaseSource = File.ReadAllText(Path.Combine(
            root, "Common", "Frontend", "ViewModels", "ViewModelBase.cs"));
        foreach (var propertyName in expectedVerticalItems.Concat(expectedHorizontalItems)
                     .Select(item => item.Item1)
                     .Distinct(StringComparer.Ordinal))
        {
            Assert.IsNotNull(viewModelBaseType.GetProperty(propertyName),
                $"ViewModelBase is missing localized ScrollBar label property {propertyName}.");
            StringAssert.Contains(viewModelBaseSource,
                $"RaisePropertyChanged(nameof({propertyName}))");
        }
        foreach (var localeFile in new[] { "en_us.json", "hu.json" })
        {
            using var translations = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                root, "Common", "Frontend", "Assets", "Localization", localeFile)));
            foreach (var key in expectedLocalizationKeys)
            {
                Assert.IsTrue(translations.RootElement.TryGetProperty(key, out var value),
                    $"{localeFile} is missing app-owned ScrollBar label {key}.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(value.GetString()),
                    $"{localeFile} has an empty app-owned ScrollBar label {key}.");
            }
        }

        foreach (var themeName in new[] { "Dark", "Light" })
        {
            var themeDictionary = XDocument.Load(Path.Combine(
                root, "Common", "Frontend", "Assets", "Themes", themeName, "Theme.axaml"));
            var resourceKeys = themeDictionary.Descendants()
                .Attributes(xaml + "Key")
                .Select(attribute => attribute.Value)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var resourceKey in new[]
                     {
                         "AppBrush.Scrollbar.Track", "AppBrush.Scrollbar.Thumb",
                         "AppBrush.Scrollbar.ThumbHover", "AppBrush.Scrollbar.ThumbPressed"
                     })
            {
                Assert.IsTrue(resourceKeys.Contains(resourceKey),
                    $"{themeName} is missing {resourceKey}.");
            }
        }
    }

    private static bool HasSetter(XElement style, XNamespace avalonia, string property, string value) =>
        style.Elements(avalonia + "Setter").Any(setter =>
            (string?)setter.Attribute("Property") == property &&
            (string?)setter.Attribute("Value") == value);

    private static string GetRepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "..", ".."));
}
