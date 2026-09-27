namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public sealed record UiActivationRequestDto(
    UiActivationReason Reason,
    bool BringToForeground,
    UiActivationCommand Command = UiActivationCommand.Activate);
