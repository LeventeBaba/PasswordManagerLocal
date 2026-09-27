using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PasswordManagerLocal.Common.Frontend.Localization;

namespace PasswordManagerLocal.Common.Frontend.Services;

public static class FirewallPermissionStartupPrompt
{
    private static readonly SemaphoreSlim PromptLock = new(1, 1);
    private static WeakReference<Window>? _activeOwner;

    public static void SetActiveTopLevel(TopLevel? topLevel)
    {
        _activeOwner = topLevel is Window window
            ? new WeakReference<Window>(window)
            : null;
    }



    public static async Task TryShowAsync(Window owner, AppLanguage language, CancellationToken ct = default)
    {
        await EnsureConfiguredAsync(owner, language, false, ct);
    }



    public static Task<bool> EnsureConfiguredAsync(AppLanguage language, CancellationToken ct = default) =>
        EnsureConfiguredAsync(ResolveOwner(), language, false, ct);



    public static async Task RevalidateAfterLikelyFirewallFailureAsync(
        Exception exception,
        AppLanguage language,
        CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() ||
            !FirewallFailureDetector.IsLikelyFirewallRelated(exception))
        {
            return;
        }

        try
        {
            await EnsureConfiguredAsync(ResolveOwner(), language, true, ct);
        }
        catch
        {
        }
    }



    private static async Task<bool> EnsureConfiguredAsync(
        Window? owner,
        AppLanguage language,
        bool forceVerification,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return true;

        if (!forceVerification && WindowsFirewallConfigurationStore.IsConfigured())
            return true;

        await PromptLock.WaitAsync(ct);
        try
        {
            if (!forceVerification && WindowsFirewallConfigurationStore.IsConfigured())
                return true;

            var check = await CheckFirewallAsync(ct);
            if (!check.IsSupported)
                return true;

            if (check.IsConfigured)
            {
                WindowsFirewallConfigurationStore.SetConfigured(true);
                return true;
            }

            WindowsFirewallConfigurationStore.SetConfigured(false);

            if (!check.CanRequestPermission || owner is null)
                return false;

            var approved = await RunOnUiThreadAsync(() => ShowConfirmDialogAsync(owner, language));
            if (!approved)
                return false;

            var result = await RequestFirewallPermissionAsync(ct);
            WindowsFirewallConfigurationStore.SetConfigured(result.IsConfigured);

            if (!result.IsConfigured)
                await RunOnUiThreadAsync(() => ShowErrorDialogAsync(owner, language, result.Details));

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



    private static Task RunOnUiThreadAsync(Func<Task> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return action();

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await action();
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }



    private static async Task<bool> ShowConfirmDialogAsync(Window owner, AppLanguage language)
    {
        var result = false;
        var dialog = BuildDialog(language, "Firewall_Title", "Firewall_Message", "Firewall_Allow", "Common_Cancel", close => result = close);
        await dialog.ShowDialog(owner);
        return result;
    }



    private static async Task ShowErrorDialogAsync(Window owner, AppLanguage language, string? details)
    {
        var message = T(language, "Firewall_Failed");
        if (!string.IsNullOrWhiteSpace(details))
            message = $"{message}\n\n{details}";

        var dialog = BuildDialog(language, "Firewall_Title", message, "Common_Ok", null, _ => { }, isMessageKey: false);
        await dialog.ShowDialog(owner);
    }



    private static Window BuildDialog(
        AppLanguage language,
        string titleKey,
        string messageKeyOrText,
        string primaryButtonKey,
        string? secondaryButtonKey,
        Action<bool> setResult,
        bool isMessageKey = true)
    {
        var dialog = CreateDialogWindow(language, titleKey);
        var title = CreateDialogTitle(language, titleKey);
        var message = CreateDialogMessage(language, messageKeyOrText, isMessageKey);
        var buttons = CreateDialogButtons(
            dialog,
            language,
            primaryButtonKey,
            secondaryButtonKey,
            setResult);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Children = { title, message, buttons }
        };

        AddDialogKeyboardShortcuts(dialog, secondaryButtonKey is not null, setResult);
        return dialog;
    }

    private static void AddDialogKeyboardShortcuts(
        Window dialog,
        bool hasSecondaryButton,
        Action<bool> setResult)
    {
        dialog.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) =>
            {
                if (e.KeyModifiers != KeyModifiers.None)
                    return;

                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    setResult(true);
                    dialog.Close();
                    return;
                }

                if (e.Key == Key.Escape && hasSecondaryButton)
                {
                    e.Handled = true;
                    setResult(false);
                    dialog.Close();
                }
            },
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    private static Window CreateDialogWindow(AppLanguage language, string titleKey) =>
        new()
        {
            Title = T(language, titleKey),
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

    private static TextBlock CreateDialogTitle(AppLanguage language, string titleKey) =>
        new()
        {
            Text = T(language, titleKey),
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };

    private static TextBlock CreateDialogMessage(
        AppLanguage language,
        string messageKeyOrText,
        bool isMessageKey) =>
        new()
        {
            Text = isMessageKey ? T(language, messageKeyOrText) : messageKeyOrText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0)
        };

    private static StackPanel CreateDialogButtons(
        Window dialog,
        AppLanguage language,
        string primaryButtonKey,
        string? secondaryButtonKey,
        Action<bool> setResult)
    {
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 24, 0, 0)
        };

        if (secondaryButtonKey is not null)
            buttons.Children.Add(CreateResultButton(dialog, language, secondaryButtonKey, false, setResult));

        buttons.Children.Add(CreateResultButton(dialog, language, primaryButtonKey, true, setResult));
        return buttons;
    }

    private static Button CreateResultButton(
        Window dialog,
        AppLanguage language,
        string buttonKey,
        bool result,
        Action<bool> setResult)
    {
        var button = new Button
        {
            Content = T(language, buttonKey),
            MinWidth = 120,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        button.Click += (_, _) =>
        {
            setResult(result);
            dialog.Close();
        };
        return button;
    }



    private static string T(AppLanguage language, string key) =>
        LocalizationManager.GetString(language, key);
}
