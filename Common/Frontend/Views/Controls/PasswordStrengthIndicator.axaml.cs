using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PasswordManagerLocal.Common.Frontend.Views.Controls;

public partial class PasswordStrengthIndicator : UserControl
{
    private static readonly IBrush InactiveBrush = new SolidColorBrush(Color.Parse("#33808080"));
    private static readonly IBrush WeakBrush = new SolidColorBrush(Color.Parse("#FFE5484D"));
    private static readonly IBrush ModerateBrush = new SolidColorBrush(Color.Parse("#FFF59E0B"));
    private static readonly IBrush GoodBrush = new SolidColorBrush(Color.Parse("#FF84CC16"));
    private static readonly IBrush StrongBrush = new SolidColorBrush(Color.Parse("#FF10B981"));
    private static readonly IBrush ExcellentBrush = new SolidColorBrush(Color.Parse("#FF2D6AE3"));

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
        var activeBrush = GetActiveBrush(strength);

        _strengthScoreText.Text = $"{strength}/10";

        for (var index = 0; index < _segments.Length; index++)
            _segments[index].Background = index < strength ? activeBrush : InactiveBrush;
    }

    private static IBrush GetActiveBrush(int strength) => strength switch
    {
        <= 3 => WeakBrush,
        <= 5 => ModerateBrush,
        <= 7 => GoodBrush,
        <= 9 => StrongBrush,
        _ => ExcellentBrush
    };
}
