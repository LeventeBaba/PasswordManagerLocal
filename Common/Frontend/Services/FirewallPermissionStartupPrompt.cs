using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace PasswordManagerLocal.Common.Frontend.Services;

public static class FirewallPermissionStartupPrompt
{
    private static readonly SemaphoreSlim PromptLock = new(1, 1);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);
    private static WeakReference<Window>? _activeOwner;

    private enum DialogDecision
    {
        Dismissed,
        Allow,
        Configured
    }

    public static void SetActiveTopLevel(TopLevel? topLevel)
    {
        _activeOwner = topLevel is Window window
            ? new WeakReference<Window>(window)
            : null;
    }



    public static async Task TryShowAsync(
        Window owner,
        UiPreferencesService preferences,
        CancellationToken ct = default)
    {
        await EnsureConfiguredAsync(owner, preferences, ct);
    }



    public static Task<bool> EnsureConfiguredAsync(
        UiPreferencesService preferences,
        CancellationToken ct = default) =>
        EnsureConfiguredAsync(ResolveOwner(), preferences, ct);



    public static async Task RevalidateAfterLikelyFirewallFailureAsync(
        Exception exception,
        UiPreferencesService preferences,
        CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() ||
            !FirewallFailureDetector.IsLikelyFirewallRelated(exception))
        {
            return;
        }

        try
        {
            await EnsureConfiguredAsync(ResolveOwner(), preferences, ct);
        }
        catch
        {
        }
    }



    private static async Task<bool> EnsureConfiguredAsync(
        Window? owner,
        UiPreferencesService preferences,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return true;

        await PromptLock.WaitAsync(ct);
        try
        {
            // A saved success flag cannot prove that rules still match the Agent
            // path, current ports, or effective Windows network policy.
            var check = await CheckFirewallAsync(ct);
            if (!check.IsSupported)
                return true;

            if (check.IsConfigured)
            {
                WindowsFirewallConfigurationStore.SetConfigured(true);
                return true;
            }

            WindowsFirewallConfigurationStore.SetConfigured(false);

            if (owner is null)
                return false;
            var decision = await RunOnUiThreadAsync(
                () => ShowFirewallDialogAsync(owner, preferences, check, monitorPolicy: true, ct));
            if (decision == DialogDecision.Configured)
            {
                WindowsFirewallConfigurationStore.SetConfigured(true);
                return true;
            }

            if (decision != DialogDecision.Allow)
                return false;

            var result = await RequestFirewallPermissionAsync(ct);
            WindowsFirewallConfigurationStore.SetConfigured(result.IsConfigured);

            if (!result.IsConfigured)
            {
                await RunOnUiThreadAsync(
                    () => ShowFirewallDialogAsync(owner, preferences, result, monitorPolicy: false, ct));
            }

            return result.IsConfigured;
        }
        finally
        {
            PromptLock.Release();
        }
    }



    private static async Task<FirewallPermissionCheckResult> CheckFirewallAsync(CancellationToken ct)
    {
        try
        {
            return await FirewallPermissionService.CheckAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CreateFailedCheckResult(ex);
        }
    }



    private static async Task<FirewallPermissionCheckResult> RequestFirewallPermissionAsync(
        CancellationToken ct)
    {
        try
        {
            return await FirewallPermissionService.RequestPermissionAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CreateFailedCheckResult(ex);
        }
    }



    private static FirewallPermissionCheckResult CreateFailedCheckResult(Exception exception) =>
        new()
        {
            IsSupported = true,
            IsConfigured = false,
            CanRequestPermission = true,
            Details = exception.Message
        };



    private static Window? ResolveOwner()
    {
        if (_activeOwner is not null && _activeOwner.TryGetTarget(out var owner))
            return owner;

        return Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
    }



    private static Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return action();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                completion.TrySetResult(await action());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }



    private static async Task<DialogDecision> ShowFirewallDialogAsync(
        Window owner,
        UiPreferencesService preferences,
        FirewallPermissionCheckResult check,
        bool monitorPolicy,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var decision = DialogDecision.Dismissed;
        var dialog = new Window
        {
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        var title = new TextBlock
        {
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        var message = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var dismiss = CreateDialogButton();
        dismiss.IsCancel = true;
        dismiss.Click += (_, _) => dialog.Close();

        var allow = CreateDialogButton();
        allow.Click += (_, _) =>
        {
            decision = DialogDecision.Allow;
            dialog.Close();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 24, 0, 0),
            Children = { dismiss, allow }
        };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Children = { title, message, buttons }
        };

        void Render()
        {
            var canAllow = monitorPolicy && check.CanRequestPermission;
            dialog.Title = preferences.GetString("Firewall_Title");
            title.Text = dialog.Title;
            if (canAllow)
            {
                message.Text = preferences.GetString("Firewall_Message");
            }
            else
            {
                var details = preferences.GetString("Firewall_Failed");
                if (!string.IsNullOrWhiteSpace(check.Details))
                    details += "\n\n" + check.Details;
                if (monitorPolicy)
                    details += "\n\n" + preferences.GetString("Firewall_RecheckHint");
                message.Text = details;
            }

            dismiss.Content = preferences.GetString(canAllow ? "Common_Cancel" : "Common_Ok");
            dismiss.IsDefault = !canAllow;
            allow.Content = preferences.GetString("Firewall_Allow");
            allow.IsVisible = canAllow;
            allow.IsDefault = canAllow;
        }

        void ApplyCheck(FirewallPermissionCheckResult updated)
        {
            if (updated.IsConfigured || !updated.IsSupported)
            {
                decision = DialogDecision.Configured;
                dialog.Close();
            }
            else if (check.CanRequestPermission != updated.CanRequestPermission ||
                !string.Equals(check.Details, updated.Details, StringComparison.Ordinal))
            {
                check = updated;
                Render();
            }
        }

        void OnPreferencesChanged(object? sender, UiPreferencesChangedEventArgs args)
        {
            if (args.LanguageChanged)
                Render();
        }

        Render();
        using var dialogLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        dialog.Closed += (_, _) => dialogLifetime.Cancel();
        using var closeOnCancellation = ct.Register(() => Dispatcher.UIThread.Post(() =>
        {
            if (dialog.IsVisible)
                dialog.Close();
        }));
        preferences.PreferencesChanged += OnPreferencesChanged;
        try
        {
            var closed = dialog.ShowDialog(owner);
            var monitoring = monitorPolicy
                ? MonitorFirewallAsync(CheckFirewallAsync, ApplyCheck, RefreshInterval, dialogLifetime.Token)
                : Task.CompletedTask;
            try
            {
                await closed;
            }
            finally
            {
                dialogLifetime.Cancel();
                try { await monitoring; }
                catch (OperationCanceledException) when (dialogLifetime.IsCancellationRequested) { }
            }

            ct.ThrowIfCancellationRequested();
            return decision;
        }
        finally
        {
            preferences.PreferencesChanged -= OnPreferencesChanged;
        }
    }

    internal static async Task MonitorFirewallAsync(
        Func<CancellationToken, Task<FirewallPermissionCheckResult>> checkAsync,
        Action<FirewallPermissionCheckResult> applyCheck,
        TimeSpan interval,
        CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(interval, ct);
            var check = await checkAsync(ct);
            ct.ThrowIfCancellationRequested();
            applyCheck(check);
            if (check.IsConfigured || !check.IsSupported)
                return;
        }
    }

    private static Button CreateDialogButton() =>
        new()
        {
            MinWidth = 120,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
}
