using Avalonia.Media;
using PasswordManagerLocal.Common.Contracts.Devices;
using PasswordManagerLocal.Common.Contracts.Authentication;
using PasswordManagerLocal.Common.Contracts.Security;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Frontend.Services;
using ReactiveUI;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

public sealed class DeviceItemViewModel : ReactiveObject
{
    private static readonly IBrush OnlineStatusBrush = new SolidColorBrush(Color.Parse("#FF2E7D32"));
    private static readonly IBrush BlockedStatusBrush = new SolidColorBrush(Color.Parse("#FFD13438"));
    private static readonly IBrush OfflineStatusBrush = new SolidColorBrush(Color.Parse("#FFF59E0B"));

    private string _editableName = string.Empty;
    private bool _isSyncOn;
    private string _currentDeviceLabel = string.Empty;
    private string _blockedLabel = string.Empty;
    private string _trustedLabel = string.Empty;
    private string _notTrustedLabel = string.Empty;
    private string _syncEnabledLabel = string.Empty;
    private string _syncDisabledLabel = string.Empty;
    private string _onlineLabel = string.Empty;
    private string _offlineLabel = string.Empty;
    private string _syncToggleOnLabel = string.Empty;
    private string _syncToggleOffLabel = string.Empty;
    private string _windowsPcLabel = string.Empty;
    private string _androidMobileLabel = string.Empty;
    private string _unknownDeviceTypeLabel = string.Empty;
    private string _saveNameLabel = string.Empty;
    private string _unblockLabel = string.Empty;
    private string _disconnectLabel = string.Empty;
    private string _deviceNameLabel = string.Empty;
    private string _deviceLastLoginDateLabel = string.Empty;
    private string _devicePreviousLoginDateLabel = string.Empty;
    private string _deviceLastSyncLabel = string.Empty;
    private string _deviceLinkedAtLabel = string.Empty;
    private string _deviceBlockedReasonLabel = string.Empty;
    private string _deviceBlockedAtLabel = string.Empty;
    private string _deviceInvalidAttemptsLabel = string.Empty;

    private DeviceItemViewModel(
        UserDeviceInfoResponse device,
        DeviceItemLocalization localization,
        ProfileViewModel owner)
    {
        Owner = owner;
        DeviceId = device.DeviceId;
        DeviceType = device.DeviceType;
        Name = device.Name;
        EditableName = device.Name;
        LastSync = device.LastSync;
        LastLoginDate = device.LastLoginDate;
        PreviousLoginDate = device.PreviousLoginDate;
        IsTrusted = device.IsTrusted;
        IsBlocked = device.IsBlocked;
        BlockedReason = device.BlockedReason;
        BlockedAt = device.BlockedAt;
        InvalidSyncAttemptCount = device.InvalidSyncAttemptCount;
        IsSyncOn = device.IsSyncOn;
        IsOnline = device.IsOnline;
        LinkedAt = device.LinkedAt;
        IsCurrentDevice = device.IsCurrentDevice;
        AssignLocalization(localization);
    }

    public ProfileViewModel Owner { get; }

    public Guid DeviceId { get; }

    public DeviceType DeviceType { get; }

    public string Name { get; private set; }

    public string EditableName
    {
        get => _editableName;
        set => this.RaiseAndSetIfChanged(ref _editableName, value);
    }

    public DateTime? LastSync { get; }

    public DateTime? LastLoginDate { get; }

    public DateTime? PreviousLoginDate { get; }

    public bool IsTrusted { get; }

    public bool IsBlocked { get; }

    public string? BlockedReason { get; }

    public DateTimeOffset? BlockedAt { get; }

    public int InvalidSyncAttemptCount { get; }

    public bool IsSyncOn
    {
        get => _isSyncOn;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isSyncOn, value);
            this.RaisePropertyChanged(nameof(SyncStateText));
        }
    }

    public bool IsOnline { get; }

    public bool IsOffline => !IsOnline;

    public DateTimeOffset LinkedAt { get; }

    public bool IsCurrentDevice { get; }

    public bool IsRemoteDevice => !IsCurrentDevice;

    public bool ShowNotTrustedStatus => IsRemoteDevice && !IsTrusted;

    public bool CanDisconnect => IsRemoteDevice;

    public bool CanUnblock => IsBlocked && IsRemoteDevice;

    public bool HasBlockedReason => !string.IsNullOrWhiteSpace(BlockedReason);

    public bool HasBlockedAt => BlockedAt is not null;

    public bool HasInvalidSyncAttempts => IsRemoteDevice && InvalidSyncAttemptCount > 0;

    public bool HasLinkedAt => LinkedAt != default;

    public bool ShowLastLoginDate => LastLoginDate is not null;

    public bool ShowPreviousLoginDate => PreviousLoginDate is not null;

    public bool ShowRemoteLastSync => IsRemoteDevice && LastSync is not null;

    public bool ShowOnlineStatus => !IsBlocked && IsOnline;

    public bool ShowOfflineStatus => !IsBlocked && IsOffline;

    public IBrush StatusIndicatorBrush => IsBlocked
        ? BlockedStatusBrush
        : IsOnline
            ? OnlineStatusBrush
            : OfflineStatusBrush;

    public string CurrentDeviceLabel => _currentDeviceLabel;

    public string BlockedLabel => _blockedLabel;

    public string TrustedLabel => _trustedLabel;

    public string NotTrustedLabel => _notTrustedLabel;

    public string SyncEnabledLabel => _syncEnabledLabel;

    public string SyncDisabledLabel => _syncDisabledLabel;

    public string OnlineLabel => _onlineLabel;

    public string OfflineLabel => _offlineLabel;

    public string SyncToggleOnLabel => _syncToggleOnLabel;

    public string SyncToggleOffLabel => _syncToggleOffLabel;

    public string DeviceTypeText => DeviceType switch
    {
        PasswordManagerLocal.Common.Contracts.Devices.DeviceType.WindowsPc => _windowsPcLabel,
        PasswordManagerLocal.Common.Contracts.Devices.DeviceType.AndroidMobile => _androidMobileLabel,
        _ => _unknownDeviceTypeLabel
    };

    public string SaveNameLabel => _saveNameLabel;

    public string UnblockLabel => _unblockLabel;

    public string DisconnectLabel => _disconnectLabel;

    public string DeviceNameLabel => _deviceNameLabel;

    public string DeviceLastLoginDateLabel => _deviceLastLoginDateLabel;

    public string DevicePreviousLoginDateLabel => _devicePreviousLoginDateLabel;

    public string DeviceLastSyncLabel => _deviceLastSyncLabel;

    public string DeviceLinkedAtLabel => _deviceLinkedAtLabel;

    public string DeviceBlockedReasonLabel => _deviceBlockedReasonLabel;

    public string DeviceBlockedAtLabel => _deviceBlockedAtLabel;

    public string DeviceInvalidAttemptsLabel => _deviceInvalidAttemptsLabel;

    public string TrustStateText => IsTrusted ? TrustedLabel : NotTrustedLabel;

    public string SyncStateText => IsSyncOn ? SyncEnabledLabel : SyncDisabledLabel;

    public string OnlineStateText => IsOnline ? OnlineLabel : OfflineLabel;

    public string LastSyncText => LastSync is { } lastSync
        ? FrontendDateTimeUtil.ToLocalFromBackendUtc(lastSync).ToString("g")
        : string.Empty;

    public string LastLoginDateText => LastLoginDate is { } lastLoginDate
        ? FrontendDateTimeUtil.ToLocalFromBackendUtc(lastLoginDate).ToString("g")
        : string.Empty;

    public string PreviousLoginDateText => PreviousLoginDate is { } previousLoginDate
        ? FrontendDateTimeUtil.ToLocalFromBackendUtc(previousLoginDate).ToString("g")
        : string.Empty;

    public string LinkedAtText => FrontendDateTimeUtil.ToLocalFromBackendUtc(LinkedAt).ToString("g");

    public string BlockedAtText => FrontendDateTimeUtil.ToLocalFromBackendUtc(BlockedAt)?.ToString("g") ?? string.Empty;

    public void ApplySyncState(bool isSyncOn) =>
        IsSyncOn = isSyncOn;

    public void ApplySavedName(string name)
    {
        Name = name;
        EditableName = name;
        this.RaisePropertyChanged(nameof(Name));
    }

    public void ApplyLocalization(DeviceItemLocalization localization)
    {
        AssignLocalization(localization);
        RaiseLocalizedPropertiesChanged();
    }

    private void AssignLocalization(DeviceItemLocalization localization)
    {
        _currentDeviceLabel = localization.CurrentDeviceLabel;
        _blockedLabel = localization.BlockedLabel;
        _trustedLabel = localization.TrustedLabel;
        _notTrustedLabel = localization.NotTrustedLabel;
        _syncEnabledLabel = localization.SyncEnabledLabel;
        _syncDisabledLabel = localization.SyncDisabledLabel;
        _onlineLabel = localization.OnlineLabel;
        _offlineLabel = localization.OfflineLabel;
        _syncToggleOnLabel = localization.SyncToggleOnLabel;
        _syncToggleOffLabel = localization.SyncToggleOffLabel;
        _windowsPcLabel = localization.WindowsPcLabel;
        _androidMobileLabel = localization.AndroidMobileLabel;
        _unknownDeviceTypeLabel = localization.UnknownDeviceTypeLabel;
        _saveNameLabel = localization.SaveNameLabel;
        _unblockLabel = localization.UnblockLabel;
        _disconnectLabel = localization.DisconnectLabel;
        _deviceNameLabel = localization.DeviceNameLabel;
        _deviceLastLoginDateLabel = localization.DeviceLastLoginDateLabel;
        _devicePreviousLoginDateLabel = localization.DevicePreviousLoginDateLabel;
        _deviceLastSyncLabel = localization.DeviceLastSyncLabel;
        _deviceLinkedAtLabel = IsCurrentDevice
            ? localization.CurrentDeviceLinkedAtLabel
            : localization.RemoteDeviceLinkedAtLabel;
        _deviceBlockedReasonLabel = localization.DeviceBlockedReasonLabel;
        _deviceBlockedAtLabel = localization.DeviceBlockedAtLabel;
        _deviceInvalidAttemptsLabel = localization.DeviceInvalidAttemptsLabel;
    }

    private void RaiseLocalizedPropertiesChanged()
    {
        this.RaisePropertyChanged(nameof(CurrentDeviceLabel));
        this.RaisePropertyChanged(nameof(BlockedLabel));
        this.RaisePropertyChanged(nameof(TrustedLabel));
        this.RaisePropertyChanged(nameof(NotTrustedLabel));
        this.RaisePropertyChanged(nameof(SyncEnabledLabel));
        this.RaisePropertyChanged(nameof(SyncDisabledLabel));
        this.RaisePropertyChanged(nameof(OnlineLabel));
        this.RaisePropertyChanged(nameof(OfflineLabel));
        this.RaisePropertyChanged(nameof(SyncToggleOnLabel));
        this.RaisePropertyChanged(nameof(SyncToggleOffLabel));
        this.RaisePropertyChanged(nameof(DeviceTypeText));
        this.RaisePropertyChanged(nameof(SaveNameLabel));
        this.RaisePropertyChanged(nameof(UnblockLabel));
        this.RaisePropertyChanged(nameof(DisconnectLabel));
        this.RaisePropertyChanged(nameof(DeviceNameLabel));
        this.RaisePropertyChanged(nameof(DeviceLastLoginDateLabel));
        this.RaisePropertyChanged(nameof(DevicePreviousLoginDateLabel));
        this.RaisePropertyChanged(nameof(DeviceLastSyncLabel));
        this.RaisePropertyChanged(nameof(DeviceLinkedAtLabel));
        this.RaisePropertyChanged(nameof(DeviceBlockedReasonLabel));
        this.RaisePropertyChanged(nameof(DeviceBlockedAtLabel));
        this.RaisePropertyChanged(nameof(DeviceInvalidAttemptsLabel));
        this.RaisePropertyChanged(nameof(TrustStateText));
        this.RaisePropertyChanged(nameof(SyncStateText));
        this.RaisePropertyChanged(nameof(OnlineStateText));
    }

    public static DeviceItemViewModel Create(
        UserDeviceInfoResponse device,
        DeviceItemLocalization localization,
        ProfileViewModel owner) =>
        new(
            device,
            localization,
            owner);
}
