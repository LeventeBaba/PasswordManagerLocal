namespace PasswordManagerLocal.Common.Frontend.Information;

public sealed class InformationNavigationHistory
{
    private readonly Stack<InformationRoute> _previous = new();

    public InformationRoute? Current { get; private set; }

    public void Reset(InformationRoute route)
    {
        _previous.Clear();
        Current = route;
    }

    public void Navigate(InformationRoute route)
    {
        if (route == Current)
            return;
        if (Current is not null)
            _previous.Push(Current);
        Current = route;
    }

    public bool TryGoBack(out InformationRoute? route)
    {
        if (!_previous.TryPop(out route))
            return false;
        Current = route;
        return true;
    }

    public void Clear()
    {
        _previous.Clear();
        Current = null;
    }
}
