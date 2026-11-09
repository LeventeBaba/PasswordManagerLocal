using Avalonia;

namespace PasswordManagerLocal.Common.Frontend.Views.Behaviors;

internal static class TouchGestureIntentClassifier
{
    private const double EarlyHorizontalLockDistance = 5;
    private const double DirectionLockDistance = 10;
    private const double EarlyHorizontalDominanceRatio = 1.2;
    private const double HorizontalDominanceRatio = 1.15;

    public static TouchGestureIntent Classify(Vector movement)
    {
        var absoluteX = Math.Abs(movement.X);
        var absoluteY = Math.Abs(movement.Y);

        if (absoluteX >= EarlyHorizontalLockDistance &&
            absoluteX >= absoluteY * EarlyHorizontalDominanceRatio)
        {
            return TouchGestureIntent.Horizontal;
        }

        if (Math.Max(absoluteX, absoluteY) < DirectionLockDistance)
            return TouchGestureIntent.Undetermined;

        if (absoluteX >= absoluteY * HorizontalDominanceRatio)
            return TouchGestureIntent.Horizontal;

        if (absoluteY >= absoluteX)
            return TouchGestureIntent.Vertical;

        return TouchGestureIntent.Undetermined;
    }
}
