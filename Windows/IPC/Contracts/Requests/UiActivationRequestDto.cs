namespace PasswordManagerLocal.Windows.Ipc.Contracts.Requests;

public sealed record UiActivationRequestDto(
    UiActivationReason Reason,
    bool BringToForeground,
    UiActivationCommand Command = UiActivationCommand.Activate);
