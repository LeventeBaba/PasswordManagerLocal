#pragma warning disable CS0618
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Content;
using PasswordManagerLocal.Common.Frontend.Services;
using Camera = Android.Hardware.Camera;
using Color = Android.Graphics.Color;
using Orientation = Android.Content.Res.Orientation;

namespace PasswordManagerLocal.Android.Frontend;

[Activity(
    Label = "Scan QR code",
    Theme = "@style/MyTheme.NoActionBar",
    ScreenOrientation = ScreenOrientation.Portrait,
    Exported = false,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class EnrollmentQrScannerActivity : Activity, TextureView.ISurfaceTextureListener, Camera.IPreviewCallback
{
    public const string TitleExtra = "PasswordManagerLocal.EnrollmentQrScanner.Title";
    public const string DescriptionExtra = "PasswordManagerLocal.EnrollmentQrScanner.Description";
    public const string ResultEnrollmentCodeExtra = "PasswordManagerLocal.EnrollmentQrScanner.EnrollmentCode";

    private const int CameraPermissionRequestCode = 7401;
    private const int DecodeThrottleMilliseconds = 350;

    private TextureView? _previewView;
    private TextView? _statusText;
    private Camera? _camera;
    private SurfaceTexture? _activeSurfaceTexture;
    private int _cameraId = -1;
    private int _previewWidth;
    private int _previewHeight;
    private int _isDecodingFrame;
    private bool _hasResult;
    private long _lastDecodeStartedAt;
    private long _lastForeignQrStatusAt;


    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        BuildLayout();

        if (HasCameraPermission())
        {
            InitializeCameraWhenReady();
        }
        else
        {
            RequestPermissions(new[] { global::Android.Manifest.Permission.Camera }, CameraPermissionRequestCode);
        }
    }


    protected override void OnResume()
    {
        base.OnResume();

        if (HasCameraPermission())
        {
            InitializeCameraWhenReady();
        }
    }


    protected override void OnPause()
    {
        StopCamera();
        base.OnPause();
    }


    protected override void OnDestroy()
    {
        StopCamera();
        base.OnDestroy();
    }


    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        if (requestCode != CameraPermissionRequestCode)
        {
            return;
        }

        if (grantResults.Length > 0 && grantResults[0] == Permission.Granted)
        {
            InitializeCameraWhenReady();
            return;
        }

        SetStatus("Camera permission was denied. QR scanning cannot start.");
        SetResult(Result.Canceled);
    }


    public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
    {
        _activeSurfaceTexture = surface;
        StartCamera(surface);
    }


    public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
    {
        StopCamera();
        _activeSurfaceTexture = null;
        return true;
    }


    public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height)
    {
    }


    public void OnSurfaceTextureUpdated(SurfaceTexture surface)
    {
    }


    public void OnPreviewFrame(byte[]? data, Camera? camera)
    {
        if (_hasResult || data is null || data.Length == 0 || _previewWidth <= 0 || _previewHeight <= 0)
        {
            return;
        }

        var now = SystemClock.ElapsedRealtime();
        if (now - _lastDecodeStartedAt < DecodeThrottleMilliseconds)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _isDecodingFrame, 1, 0) != 0)
        {
            return;
        }

        _lastDecodeStartedAt = now;
        var frame = new byte[data.Length];
        Buffer.BlockCopy(data, 0, frame, 0, data.Length);
        var width = _previewWidth;
        var height = _previewHeight;

        _ = Task.Run(() => DecodePreviewFrame(frame, width, height));
    }


    public override void OnBackPressed()
    {
        SetResult(Result.Canceled);
        base.OnBackPressed();
    }


    private void BuildLayout()
    {
        var root = CreateRootLayout();
        root.AddView(
            CreateHeaderText(ResolveIntentText(TitleExtra, "Scan enrollment QR code"), 20, 28, 8),
            CreateWrapContentLayoutParams());
        root.AddView(
            CreateHeaderText(
                ResolveIntentText(
                    DescriptionExtra,
                    "Point the camera at the QR code shown on the new device. Only PasswordManagerLocal enrollment QR codes are accepted."),
                14,
                0,
                18),
            CreateWrapContentLayoutParams());
        root.AddView(CreatePreviewContainer(), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        _statusText = CreateStatusText();
        root.AddView(_statusText, CreateWrapContentLayoutParams());
        root.AddView(CreateCancelButton(), CreateWrapContentLayoutParams());
        SetContentView(root);
    }

    private LinearLayout CreateRootLayout()
    {
        var root = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical
        };
        root.SetBackgroundColor(Color.Rgb(18, 18, 18));
        return root;
    }

    private string ResolveIntentText(string extraName, string fallback)
    {
        var value = Intent?.GetStringExtra(extraName);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private TextView CreateHeaderText(string text, float textSize, int topPadding, int bottomPadding)
    {
        var textView = new TextView(this)
        {
            Text = text,
            TextSize = textSize,
            Gravity = GravityFlags.Center
        };
        textView.SetTextColor(textSize >= 20 ? Color.White : Color.Rgb(210, 210, 210));
        textView.SetPadding(28, topPadding, 28, bottomPadding);
        return textView;
    }

    private FrameLayout CreatePreviewContainer()
    {
        var previewContainer = new FrameLayout(this);
        _previewView = new TextureView(this)
        {
            SurfaceTextureListener = this
        };
        previewContainer.AddView(
            _previewView,
            new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        var guide = new TextView(this)
        {
            Text = "□",
            TextSize = 210,
            Gravity = GravityFlags.Center
        };
        guide.SetTextColor(Color.Argb(180, 255, 255, 255));
        previewContainer.AddView(
            guide,
            new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        return previewContainer;
    }

    private TextView CreateStatusText()
    {
        var statusText = new TextView(this)
        {
            Text = "Looking for an enrollment QR code...",
            TextSize = 14,
            Gravity = GravityFlags.Center
        };
        statusText.SetTextColor(Color.White);
        statusText.SetPadding(28, 18, 28, 10);
        return statusText;
    }

    private Button CreateCancelButton()
    {
        var cancelButton = new Button(this)
        {
            Text = "Cancel"
        };
        cancelButton.Click += (_, _) =>
        {
            SetResult(Result.Canceled);
            Finish();
        };
        return cancelButton;
    }

    private static LinearLayout.LayoutParams CreateWrapContentLayoutParams() =>
        new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);


    private bool HasCameraPermission() =>
        ContextCompat.CheckSelfPermission(this, global::Android.Manifest.Permission.Camera) == Permission.Granted;


    private void InitializeCameraWhenReady()
    {
        if (_previewView is null)
        {
            return;
        }

        if (_camera is not null)
        {
            return;
        }

        if (_previewView.IsAvailable && _previewView.SurfaceTexture is not null)
        {
            _activeSurfaceTexture = _previewView.SurfaceTexture;
            StartCamera(_previewView.SurfaceTexture);
        }
    }


    private void StartCamera(SurfaceTexture surfaceTexture)
    {
        if (!HasCameraPermission() || _camera is not null || _hasResult)
            return;

        try
        {
            OpenCamera();
            if (_camera is null)
            {
                SetStatus("The camera could not be opened.");
                return;
            }

            var parameters = ConfigureCameraParameters(_camera.GetParameters());
            _camera.SetParameters(parameters);
            StartCameraPreview(surfaceTexture, parameters);
            SetStatus("Looking for an enrollment QR code...");
        }
        catch
        {
            StopCamera();
            SetStatus("The camera could not be started.");
        }
    }

    private void OpenCamera()
    {
        _cameraId = FindBackCameraId();
        _camera = _cameraId >= 0 ? Camera.Open(_cameraId) : Camera.Open();
    }

    private Camera.Parameters ConfigureCameraParameters(Camera.Parameters parameters)
    {
        parameters.PreviewFormat = ImageFormatType.Nv21;
        var previewSize = ChoosePreviewSize(parameters.SupportedPreviewSizes);
        if (previewSize is not null)
        {
            _previewWidth = previewSize.Width;
            _previewHeight = previewSize.Height;
            parameters.SetPreviewSize(_previewWidth, _previewHeight);
        }
        else
        {
            _previewWidth = parameters.PreviewSize?.Width ?? 0;
            _previewHeight = parameters.PreviewSize?.Height ?? 0;
        }

        SetPreferredFocusMode(parameters);
        return parameters;
    }

    private static void SetPreferredFocusMode(Camera.Parameters parameters)
    {
        var supportedFocusModes = parameters.SupportedFocusModes;
        if (supportedFocusModes?.Contains(Camera.Parameters.FocusModeContinuousVideo) == true)
            parameters.FocusMode = Camera.Parameters.FocusModeContinuousVideo;
        else if (supportedFocusModes?.Contains(Camera.Parameters.FocusModeAuto) == true)
            parameters.FocusMode = Camera.Parameters.FocusModeAuto;
    }

    private void StartCameraPreview(SurfaceTexture surfaceTexture, Camera.Parameters parameters)
    {
        _camera!.SetDisplayOrientation(CalculateDisplayOrientation(_cameraId));
        _camera.SetPreviewTexture(surfaceTexture);
        _camera.SetPreviewCallback(this);
        _camera.StartPreview();
        if (parameters.FocusMode != Camera.Parameters.FocusModeAuto)
            return;

        try
        {
            _camera.AutoFocus(null);
        }
        catch
        {
        }
    }


    private void StopCamera()
    {
        var camera = _camera;
        _camera = null;

        if (camera is null)
        {
            return;
        }

        try
        {
            camera.SetPreviewCallback(null);
            camera.StopPreview();
        }
        catch
        {
        }

        try
        {
            camera.Release();
        }
        catch
        {
        }
    }


    private int FindBackCameraId()
    {
        try
        {
            for (var index = 0; index < Camera.NumberOfCameras; index++)
            {
                var info = new Camera.CameraInfo();
                Camera.GetCameraInfo(index, info);
                if (info.Facing == CameraFacing.Back)
                {
                    return index;
                }
            }
        }
        catch
        {
        }

        return -1;
    }


    private int CalculateDisplayOrientation(int cameraId)
    {
        try
        {
            if (cameraId < 0)
            {
                return Resources?.Configuration?.Orientation == Orientation.Landscape ? 0 : 90;
            }

            var info = new Camera.CameraInfo();
            Camera.GetCameraInfo(cameraId, info);

            var rotation = WindowManager?.DefaultDisplay?.Rotation ?? SurfaceOrientation.Rotation0;
            var degrees = rotation switch
            {
                SurfaceOrientation.Rotation90 => 90,
                SurfaceOrientation.Rotation180 => 180,
                SurfaceOrientation.Rotation270 => 270,
                _ => 0
            };

            return info.Facing == CameraFacing.Front
                ? (360 - ((info.Orientation + degrees) % 360)) % 360
                : (info.Orientation - degrees + 360) % 360;
        }
        catch
        {
            return 90;
        }
    }


    private Camera.Size? ChoosePreviewSize(IList<Camera.Size>? sizes)
    {
        if (sizes is null || sizes.Count == 0)
        {
            return null;
        }

        return sizes
            .OrderBy(size => Math.Abs(size.Width * size.Height - 1280 * 720))
            .ThenByDescending(size => size.Width * size.Height)
            .FirstOrDefault();
    }


    private void DecodePreviewFrame(byte[] frame, int width, int height)
    {
        try
        {
            var jpegBytes = ConvertPreviewFrameToJpeg(frame, width, height);
            if (jpegBytes is null || jpegBytes.Length == 0)
            {
                return;
            }

            var qrText = EnrollmentQrCodeService.DecodeQrCode(jpegBytes);
            if (string.IsNullOrWhiteSpace(qrText))
            {
                return;
            }

            var enrollmentCode = EnrollmentQrCodeService.ExtractEnrollmentCode(qrText, allowPlainEnrollmentCode: false);
            if (string.IsNullOrWhiteSpace(enrollmentCode))
            {
                var now = SystemClock.ElapsedRealtime();
                if (now - _lastForeignQrStatusAt > 1500)
                {
                    _lastForeignQrStatusAt = now;
                    RunOnUiThread(() => SetStatus("QR code found, but it is not a PasswordManagerLocal enrollment QR code."));
                }

                return;
            }

            RunOnUiThread(() => FinishWithEnrollmentCode(enrollmentCode));
        }
        catch
        {
        }
        finally
        {
            Interlocked.Exchange(ref _isDecodingFrame, 0);
        }
    }


    private static byte[]? ConvertPreviewFrameToJpeg(byte[] frame, int width, int height)
    {
        try
        {
            using var output = new MemoryStream();
            using var yuvImage = new YuvImage(frame, ImageFormatType.Nv21, width, height, null);
            return yuvImage.CompressToJpeg(new Rect(0, 0, width, height), 75, output)
                ? output.ToArray()
                : null;
        }
        catch
        {
            return null;
        }
    }


    private void FinishWithEnrollmentCode(string enrollmentCode)
    {
        if (_hasResult)
        {
            return;
        }

        _hasResult = true;
        StopCamera();

        var result = new Intent();
        result.PutExtra(ResultEnrollmentCodeExtra, enrollmentCode);
        SetResult(Result.Ok, result);
        Finish();
    }


    private void SetStatus(string message)
    {
        if (_statusText is not null)
        {
            _statusText.Text = message;
        }
    }
}
