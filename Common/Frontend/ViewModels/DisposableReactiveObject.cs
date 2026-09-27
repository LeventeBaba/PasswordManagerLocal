using ReactiveUI;

namespace PasswordManagerLocal.Common.Frontend.ViewModels;

/// <summary>Explicit ownership of commands/subscriptions without reflection (also safe when trimmed).</summary>
public abstract class DisposableReactiveObject : ReactiveObject, IDisposable
{
    private readonly List<IDisposable> _owned = [];
    private int _disposed;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    protected T Own<T>(T resource) where T : IDisposable
    {
        if (IsDisposed)
            resource.Dispose();
        else
            _owned.Add(resource);
        return resource;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try { DisposeManaged(); }
        finally
        {
            foreach (var resource in _owned)
                resource.Dispose();
            _owned.Clear();
            GC.SuppressFinalize(this);
        }
    }

    protected virtual void DisposeManaged() { }
}
