using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using PasswordManagerLocal.Common.Frontend.Views.Styling;

namespace PasswordManagerLocal.Common.Frontend.Views.Controls;

public partial class PasswordStrengthInfoButton : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<PasswordStrengthInfoButton, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> BodyProperty =
        AvaloniaProperty.Register<PasswordStrengthInfoButton, string>(nameof(Body), string.Empty);

    public static readonly StyledProperty<string> AccessibleLabelProperty =
        AvaloniaProperty.Register<PasswordStrengthInfoButton, string>(nameof(AccessibleLabel), string.Empty);

    private readonly Button _infoButton;
    private readonly TextBlock _tooltipTitleText;
    private readonly TextBlock _tooltipBodyText;
    private readonly TextBlock _flyoutTitleText;
    private readonly TextBlock _flyoutBodyText;

    public PasswordStrengthInfoButton()
    {
        InitializeComponent();
        _infoButton = this.FindControl<Button>("InfoButton")!;
        _tooltipTitleText = CreateTitleTextBlock();
        _tooltipBodyText = CreateBodyTextBlock();
        _flyoutTitleText = CreateTitleTextBlock();
        _flyoutBodyText = CreateBodyTextBlock();

        ToolTip.SetTip(_infoButton, CreateInfoPanel(_tooltipTitleText, _tooltipBodyText, 12));
        var flyoutScrollViewer = new ScrollViewer
        {
            MaxWidth = 380,
            MaxHeight = 420,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = CreateInfoPanel(_flyoutTitleText, _flyoutBodyText, 8)
        };
        ScrollViewerStyleHelper.ApplyComfortableScrollerStyle(flyoutScrollViewer);

        _infoButton.Flyout = new Flyout
        {
            Content = flyoutScrollViewer
        };

        UpdateText();
        UpdateAccessibility();
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public string AccessibleLabel
    {
        get => GetValue(AccessibleLabelProperty);
        set => SetValue(AccessibleLabelProperty, value);
    }

    private static Border CreateInfoPanel(TextBlock title, TextBlock body, double padding) =>
        new()
        {
            MaxWidth = 380,
            Padding = new Thickness(padding),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    title,
                    body
                }
            }
        };

    private static TextBlock CreateTitleTextBlock() =>
        new()
        {
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };

    private static TextBlock CreateBodyTextBlock() =>
        new()
        {
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TitleProperty || change.Property == BodyProperty)
            UpdateText();
        else if (change.Property == AccessibleLabelProperty)
            UpdateAccessibility();
    }

    private void UpdateText()
    {
        if (_tooltipTitleText is null)
            return;

        _tooltipTitleText.Text = Title;
        _tooltipBodyText.Text = Body;
        _flyoutTitleText.Text = Title;
        _flyoutBodyText.Text = Body;
    }

    private void UpdateAccessibility()
    {
        if (_infoButton is not null)
            AutomationProperties.SetName(_infoButton, AccessibleLabel);
    }
}
