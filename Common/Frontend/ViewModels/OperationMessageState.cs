using Avalonia.Threading;
using ReactiveUI;

namespace PasswordManagerLocal.Common.Frontend.ViewModels;

public sealed class OperationMessageState : ReactiveObject, IDisposable
{
    public static readonly TimeSpan DefaultSuccessDuration = TimeSpan.FromSeconds(10);

    private bool _disposed;
    private string? _message;
    private OperationMessageKind _kind;
    private IDisposable? _autoDismissTimer;

    public string? Message => _message;

    public OperationMessageKind Kind => _kind;

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public bool IsError => HasMessage && Kind == OperationMessageKind.Error;

    public bool IsInformation => HasMessage && Kind == OperationMessageKind.Information;

    public bool IsSuccess => HasMessage && Kind == OperationMessageKind.Success;

    public bool HasNonErrorMessage => HasMessage && !IsError;

    public void ShowInformation(string message, bool autoDismiss = false) =>
        Show(message, OperationMessageKind.Information, autoDismiss ? DefaultSuccessDuration : null);

    public void ShowSuccess(string message) =>
        Show(message, OperationMessageKind.Success, DefaultSuccessDuration);

    public void ShowError(string message) =>
        Show(message, OperationMessageKind.Error, null);

    public void Clear()
    {
        CancelAutoDismiss();
        Update(null, OperationMessageKind.None);
    }

    public void Dispose()
    {
        _disposed = true;
        Clear();
    }

    private void Show(string message, OperationMessageKind kind, TimeSpan? duration)
    {
        if (_disposed)
            return;
        CancelAutoDismiss();

        if (string.IsNullOrWhiteSpace(message))
        {
            Update(null, OperationMessageKind.None);
            return;
        }

        Update(message, kind);

        if (duration is not { } autoDismissAfter)
            return;

        _autoDismissTimer = DispatcherTimer.RunOnce(() =>
        {
            _autoDismissTimer = null;
            Update(null, OperationMessageKind.None);
        }, autoDismissAfter);
    }

    private void CancelAutoDismiss()
    {
        _autoDismissTimer?.Dispose();
        _autoDismissTimer = null;
    }

    private void Update(string? message, OperationMessageKind kind)
    {
        var messageChanged = !string.Equals(_message, message, StringComparison.Ordinal);
        var kindChanged = _kind != kind;

        if (!messageChanged && !kindChanged)
            return;

        _message = message;
        _kind = kind;

        if (messageChanged)
            this.RaisePropertyChanged(nameof(Message));

        if (kindChanged)
            this.RaisePropertyChanged(nameof(Kind));

        this.RaisePropertyChanged(nameof(HasMessage));
        this.RaisePropertyChanged(nameof(IsError));
        this.RaisePropertyChanged(nameof(IsInformation));
        this.RaisePropertyChanged(nameof(IsSuccess));
        this.RaisePropertyChanged(nameof(HasNonErrorMessage));
    }
}
