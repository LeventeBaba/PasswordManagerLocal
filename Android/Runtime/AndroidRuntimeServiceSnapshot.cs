using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;

namespace PasswordManagerLocal.Android.Runtime;

public sealed record AndroidRuntimeServiceSnapshot(
    bool HasRuntimeComposition,
    bool HasInteractiveAttachment,
    bool HasBackgroundLease,
    bool IsForeground,
    bool IsDisposed,
    bool IsRuntimeUnsafe,
    bool AcceptsInteractiveAttachments,
    BackendLifetimeReason ActiveReasons,
    AndroidServiceStartPhase ServiceStartPhase,
    bool IsRuntimeReady,
    bool RequiresUserAction,
    AndroidNotificationAvailability NotificationAvailability);
