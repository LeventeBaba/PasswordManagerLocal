using Avalonia;
using Avalonia.Controls;

namespace PasswordManagerLocal.Common.Frontend.Views.Controls;

public partial class PasswordStrengthIndicator : UserControl
{
    private static readonly string[] StrengthClasses = ["weak", "moderate", "good", "strong", "excellent"];

    public static readonly StyledProperty<int> StrengthProperty =
        AvaloniaProperty.Register<PasswordStrengthIndicator, int>(nameof(Strength));

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<PasswordStrengthIndicator, string>(nameof(Label), string.Empty);

    private readonly Border[] _segments;
    private readonly TextBlock _strengthLabelText;
    private readonly TextBlock _strengthScoreText;

    public PasswordStrengthIndicator()
    {
        InitializeComponent();
        _strengthLabelText = this.FindControl<TextBlock>("StrengthLabelText")!;
        _strengthScoreText = this.FindControl<TextBlock>("StrengthScoreText")!;
        _segments =
        [
            this.FindControl<Border>("StrengthSegment1")!,
            this.FindControl<Border>("StrengthSegment2")!,
            this.FindControl<Border>("StrengthSegment3")!,
            this.FindControl<Border>("StrengthSegment4")!,
            this.FindControl<Border>("StrengthSegment5")!,
            this.FindControl<Border>("StrengthSegment6")!,
            this.FindControl<Border>("StrengthSegment7")!,
            this.FindControl<Border>("StrengthSegment8")!,
            this.FindControl<Border>("StrengthSegment9")!,
            this.FindControl<Border>("StrengthSegment10")!
        ];

        UpdateLabel();
        UpdateSegments();
    }

    public int Strength
    {
        get => GetValue(StrengthProperty);
        set => SetValue(StrengthProperty, value);
    }

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == StrengthProperty)
            UpdateSegments();
        else if (change.Property == LabelProperty)
            UpdateLabel();
    }

    private void UpdateLabel()
    {
        if (_strengthLabelText is not null)
            _strengthLabelText.Text = Label;
    }

    private void UpdateSegments()
    {
        if (_segments is null)
            return;

        var strength = Math.Clamp(Strength, 0, 10);
        var activeClass = GetActiveClass(strength);

        _strengthScoreText.Text = $"{strength}/10";

        for (var index = 0; index < _segments.Length; index++)
        {
            var segment = _segments[index];
            foreach (var className in StrengthClasses)
            {
                segment.Classes.Remove(className);
            }

            if (index < strength)
            {
                segment.Classes.Add(activeClass);
            }
        }
    }

    private static string GetActiveClass(int strength) => strength switch
    {
        <= 3 => "weak",
        <= 5 => "moderate",
        <= 7 => "good",
        <= 9 => "strong",
        _ => "excellent"
    };
}
