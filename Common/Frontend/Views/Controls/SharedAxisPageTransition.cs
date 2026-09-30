using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Styling;

namespace PasswordManagerLocal.Common.Frontend.Views.Controls;

/// <summary>Short render-transform navigation, with no full-page opacity blending.</summary>
internal sealed class SharedAxisPageTransition : IPageTransition
{
    private const double Offset = 32;
    private TransitionRun? _activeRun;

    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        Cancel();
        if (cancellationToken.IsCancellationRequested || from is null || to is null)
        {
            if (from is not null)
                from.IsVisible = false;
            if (to is not null)
                to.IsVisible = true;
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var run = new TransitionRun(from, to, cancellation);
        _activeRun = run;
        var direction = forward ? 1d : -1d;
        from.RenderTransform = new TranslateTransform();
        to.RenderTransform = new TranslateTransform(direction * Offset, 0);
        to.IsVisible = true;
        try
        {
            await Task.WhenAll(
                CreateAnimation(0, -direction * Offset).RunAsync(from, cancellation.Token),
                CreateAnimation(direction * Offset, 0).RunAsync(to, cancellation.Token));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            // A superseded run must never reset a presenter's newer animation.
            if (ReferenceEquals(_activeRun, run))
            {
                run.Restore();
                _activeRun = null;
            }
        }
    }

    public void Cancel()
    {
        if (_activeRun is not { } run)
            return;
        _activeRun = null;
        run.Cancellation.Cancel();
        run.Restore();
    }

    private static Animation CreateAnimation(double start, double end) => new()
    {
        Duration = TimeSpan.FromMilliseconds(240),
        Easing = new CubicEaseOut(),
        Children =
        {
            new KeyFrame
            {
                Cue = new Cue(0),
                Setters = { new Setter(TranslateTransform.XProperty, start) }
            },
            new KeyFrame
            {
                Cue = new Cue(1),
                Setters = { new Setter(TranslateTransform.XProperty, end) }
            }
        }
    };

    private sealed class TransitionRun(Visual from, Visual to, CancellationTokenSource cancellation)
    {
        private readonly ITransform? _fromTransform = from.RenderTransform;
        private readonly ITransform? _toTransform = to.RenderTransform;
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public void Restore()
        {
            from.RenderTransform = _fromTransform;
            to.RenderTransform = _toTransform;
            from.IsVisible = false;
            to.IsVisible = true;
        }
    }
}
