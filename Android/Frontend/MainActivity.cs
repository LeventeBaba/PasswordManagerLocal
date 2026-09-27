using System.Diagnostics;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Views.InputMethods;
using Avalonia.Android;
using PasswordManagerLocal.Common.Frontend;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Frontend.ViewModels;
using PasswordManagerLocal.Common.Frontend.Views;

namespace PasswordManagerLocal.Android.Frontend;

[Activity(
Label = "PasswordManagerLocal.Android.Frontend",
Theme = "@style/MyTheme.NoActionBar",
Icon = "@drawable/icon",
MainLauncher = true,
Exported = true,
ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    private const int EnrollmentQrScannerRequestCode = 7301;
    private static readonly TimeSpan DuplicateBackRequestSuppressionWindow = TimeSpan.FromMilliseconds(250);

    private TaskCompletionSource<string?>? _enrollmentQrScanCompletion;
    private CancellationTokenRegistration _enrollmentQrScanCancellationRegistration;
    private bool _isHandlingBackRequest;
    private long _lastAcceptedBackRequestTimestamp;
    private AlertDialog? _backConfirmationDialog;
    private FrontendUiSession? _uiSession;
    private bool _destroyed;

    protected override void OnCreate(global::Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var application = Application as PasswordManagerLocalApplication
            ?? throw new InvalidOperationException("The Android process application is unavailable.");
        // Avalonia calls MainViewFactory inside base.OnCreate. Adopt only this Activity's
        // fresh Content; never retrieve another Activity's view from ApplicationLifetime.
        var view = Content as MainView
            ?? throw new InvalidOperationException("The Android main-view factory did not create a MainView.");
        var platforms = new FrontendPlatformServices();
        platforms.Clipboard.SetPlatformClipboardWriter(new AndroidClipboardWriter(this));
        platforms.Keyboard.SetPlatformHideAction(HideSoftwareKeyboard);
        platforms.CameraScanner.SetPlatformScanner(new AndroidQrCodeCameraScanner(this));
        var attachment = new AndroidActivityServiceAttachmentHandle(application.RuntimeServiceConnector, this);
        try
        {
            var context = new FrontendApplicationContext(
                attachment.BackendClient,
                attachment.BackgroundSyncSettingsClient,
                application.ApplicationDataDirectory,
                platformServices: platforms);
            _uiSession = new FrontendUiSession(view, context, ownsBackendClient: true);
            BackRequested += HandleBackRequested;
            _ = InitializeSessionAsync(_uiSession);
        }
        catch
        {
            platforms.Dispose();
            _ = DisposeFailedAttachmentAsync(attachment);
            throw;
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (_uiSession is { } session)
            _ = ResumeSessionAsync(session);
    }

    private static async Task InitializeSessionAsync(FrontendUiSession session)
    {
        try { await session.InitializeAsync(); }
        catch (OperationCanceledException) when (session.IsDisposed) { }
        catch (Exception exception)
        {
            Trace.TraceError($"Android frontend initialization failed: {exception.GetType().Name}");
        }
    }

    private static async Task ResumeSessionAsync(FrontendUiSession session)
    {
        try { await session.ResumeAsync(); }
        catch (OperationCanceledException) when (session.IsDisposed) { }
        catch (Exception exception)
        {
            Trace.TraceError($"Android frontend resume failed: {exception.GetType().Name}");
        }
    }

    protected override void OnPause()
    {
        _uiSession?.PlatformServices.SensitiveData.RequestHideVisibleSecrets();
        base.OnPause();
    }


    protected override void OnStop()
    {
        _uiSession?.PlatformServices.SensitiveData.RequestHideVisibleSecrets();
        base.OnStop();
    }


    protected override void OnDestroy()
    {
        _destroyed = true;
        BackRequested -= HandleBackRequested;
        _backConfirmationDialog?.Dismiss();
        _backConfirmationDialog?.Dispose();
        _backConfirmationDialog = null;
        CompleteEnrollmentQrScan(null);
        var session = Interlocked.Exchange(ref _uiSession, null);
        try
        {
            if (session is not null)
                _ = DisposeSessionAsync(session);
            Content = null;
        }
        finally { base.OnDestroy(); }
    }

    private static async Task DisposeSessionAsync(FrontendUiSession session)
    {
        try { await session.DisposeAsync(); }
        catch (Exception exception)
        {
            Trace.TraceError($"Android frontend cleanup failed: {exception.GetType().Name}");
        }
    }

    private static async Task DisposeFailedAttachmentAsync(AndroidActivityServiceAttachmentHandle attachment)
    {
        try { await attachment.BackendClient.DisposeAsync(); }
        catch { }
    }

    private async void HandleBackRequested(object? sender, AndroidBackRequestedEventArgs e)
    {
        // Always consume Android's native back request. App-level back navigation is
        // handled by MainView through the same path used by the Windows Esc key.
        e.Handled = true;

        // Avalonia/Android can deliver two BackRequested notifications for one physical
        // back action on some Android versions or emulator configurations. The existing
        // in-progress flag only blocks overlapping async calls; a fast first navigation
        // can finish before the duplicate notification arrives. Suppress only near-
        // simultaneous Android duplicates while preserving intentional repeated presses.
        var backRequestTimestamp = Stopwatch.GetTimestamp();
        if (_lastAcceptedBackRequestTimestamp != 0
            && Stopwatch.GetElapsedTime(_lastAcceptedBackRequestTimestamp, backRequestTimestamp)
                < DuplicateBackRequestSuppressionWindow)
        {
            return;
        }

        _lastAcceptedBackRequestTimestamp = backRequestTimestamp;

        if (_backConfirmationDialog is { IsShowing: true } dialog)
        {
            _backConfirmationDialog = null;
            dialog.Dismiss();
            dialog.Dispose();
            return;
        }

        if (_destroyed || _isHandlingBackRequest || _uiSession is not { IsDisposed: false } session)
        {
            return;
        }

        var mainView = session.View;
        _isHandlingBackRequest = true;

        try
        {
            var wasHandled = await mainView.HandleBackRequestAsync();
            if (!_destroyed && ReferenceEquals(_uiSession, session) && !wasHandled &&
                mainView.DataContext is MainViewModel viewModel)
                ShowRootBackConfirmation(viewModel);
        }
        finally
        {
            _isHandlingBackRequest = false;
        }
    }


    private void ShowRootBackConfirmation(MainViewModel viewModel)
    {
        if (_destroyed || _uiSession?.ViewModel != viewModel || _backConfirmationDialog?.IsShowing == true)
            return;

        _backConfirmationDialog?.Dispose();
        _backConfirmationDialog = null;

        var shouldLogout = viewModel.IsAuthenticated;
        using var builder = new AlertDialog.Builder(this)
            .SetTitle(shouldLogout ? viewModel.LogoutConfirmationTitle : viewModel.ExitConfirmationTitle)
            .SetMessage(shouldLogout ? viewModel.LogoutConfirmationMessage : viewModel.ExitConfirmationMessage)
            .SetNegativeButton(viewModel.NoLabel, (_, _) => { })
            .SetPositiveButton(viewModel.YesLabel, async (_, _) =>
            {
                if (_destroyed || _uiSession?.ViewModel != viewModel)
                    return;
                if (shouldLogout)
                    await viewModel.RequestLogoutAsync();
                else
                    FinishAffinity();
            });

        var confirmation = builder.Create();
        confirmation.DismissEvent += (_, _) =>
        {
            if (ReferenceEquals(_backConfirmationDialog, confirmation))
                _backConfirmationDialog = null;
            confirmation.Dispose();
        };
        _backConfirmationDialog = confirmation;
        confirmation.Show();
    }


    private void HideSoftwareKeyboard()
    {
        var inputMethodManager = GetSystemService(InputMethodService) as InputMethodManager;
        var windowToken = CurrentFocus?.WindowToken ?? Window?.DecorView?.WindowToken;
        if (windowToken is not null)
            inputMethodManager?.HideSoftInputFromWindow(windowToken, HideSoftInputFlags.None);
    }


    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode == EnrollmentQrScannerRequestCode)
        {
            var enrollmentCode = resultCode == Result.Ok
                ? data?.GetStringExtra(EnrollmentQrScannerActivity.ResultEnrollmentCodeExtra)
                : null;

            CompleteEnrollmentQrScan(enrollmentCode);
            return;
        }

        base.OnActivityResult(requestCode, resultCode, data);
    }


    internal Task<string?> ScanEnrollmentQrCodeAsync(
        string? title = null,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        if (_destroyed || cancellationToken.IsCancellationRequested || _enrollmentQrScanCompletion is not null)
            return Task.FromResult<string?>(null);

        try
        {
            var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _enrollmentQrScanCompletion = completion;
            if (cancellationToken.CanBeCanceled)
                _enrollmentQrScanCancellationRegistration = cancellationToken.Register(
                    () => RunOnUiThread(() =>
                    {
                        if (ReferenceEquals(_enrollmentQrScanCompletion, completion))
                            CompleteEnrollmentQrScan(null);
                    }));
            cancellationToken.ThrowIfCancellationRequested();

            var intent = new Intent(this, typeof(EnrollmentQrScannerActivity));
            if (!string.IsNullOrWhiteSpace(title))
                intent.PutExtra(EnrollmentQrScannerActivity.TitleExtra, title);

            if (!string.IsNullOrWhiteSpace(description))
                intent.PutExtra(EnrollmentQrScannerActivity.DescriptionExtra, description);

            StartActivityForResult(intent, EnrollmentQrScannerRequestCode);
            return completion.Task;
        }
        catch
        {
            CompleteEnrollmentQrScan(null);
            return Task.FromResult<string?>(null);
        }
    }


    private void CompleteEnrollmentQrScan(string? enrollmentCode)
    {
        var completion = _enrollmentQrScanCompletion;

        _enrollmentQrScanCompletion = null;
        _enrollmentQrScanCancellationRegistration.Dispose();
        _enrollmentQrScanCancellationRegistration = default;

        completion?.TrySetResult(enrollmentCode);
    }

}
