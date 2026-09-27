using Avalonia.Media.Imaging;
using SkiaSharp;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;
using ZXing.SkiaSharp;

namespace PasswordManagerLocal.Common.Frontend.Services;

public static class EnrollmentQrCodeService
{
    private const string EnrollmentPayloadPrefix = "pml-enrollment-v1:";
    private const string EnrollmentPayloadUriPrefix = "passwordmanagerlocal://enrollment?code=";

    public static Bitmap? CreateQrCodeBitmap(string? enrollmentCode, int size = 260)
    {
        if (string.IsNullOrWhiteSpace(enrollmentCode))
        {
            return null;
        }

        var writer = new ZXing.SkiaSharp.BarcodeWriter
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new QrCodeEncodingOptions
            {
                CharacterSet = "UTF-8",
                ErrorCorrection = ErrorCorrectionLevel.M,
                Height = size,
                Margin = 2,
                Width = size
            }
        };

        using var qrBitmap = writer.Write(CreateEnrollmentQrPayload(enrollmentCode));
        return ToAvaloniaBitmap(qrBitmap);
    }



    public static string CreateEnrollmentQrPayload(string enrollmentCode) =>
        $"{EnrollmentPayloadPrefix}{enrollmentCode.Trim()}";



    public static string? DecodeQrCode(byte[]? imageBytes)
    {
        if (imageBytes is null || imageBytes.Length == 0)
        {
            return null;
        }

        using var imageData = SKData.CreateCopy(imageBytes);
        using var bitmap = SKBitmap.Decode(imageData);

        if (bitmap is null)
        {
            return null;
        }

        var reader = new ZXing.SkiaSharp.BarcodeReader
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                CharacterSet = "UTF-8",
                PossibleFormats = [BarcodeFormat.QR_CODE],
                TryHarder = true
            }
        };

        var result = reader.Decode(bitmap);
        return string.IsNullOrWhiteSpace(result?.Text)
            ? null
            : result.Text.Trim();
    }



    public static string? DecodeEnrollmentCodeFromQrImage(byte[]? imageBytes, bool allowPlainEnrollmentCode = true)
    {
        var qrText = DecodeQrCode(imageBytes);
        return ExtractEnrollmentCode(qrText, allowPlainEnrollmentCode);
    }



    public static string? ExtractEnrollmentCode(string? qrText, bool allowPlainEnrollmentCode = true)
    {
        if (string.IsNullOrWhiteSpace(qrText))
            return null;

        var text = qrText.Trim();
        if (TryExtractPrefixedPayload(text, out var enrollmentCode))
            return enrollmentCode;
        if (TryExtractDirectUriPayload(text, out enrollmentCode))
            return enrollmentCode;
        if (TryExtractEnrollmentUriQuery(text, out enrollmentCode))
            return enrollmentCode;

        return allowPlainEnrollmentCode && LooksLikeEnrollmentCode(text) ? text : null;
    }

    private static bool TryExtractPrefixedPayload(string text, out string? enrollmentCode)
    {
        enrollmentCode = null;
        if (!text.StartsWith(EnrollmentPayloadPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        enrollmentCode = NormalizeEnrollmentCode(text[EnrollmentPayloadPrefix.Length..]);
        return true;
    }

    private static bool TryExtractDirectUriPayload(string text, out string? enrollmentCode)
    {
        enrollmentCode = null;
        if (!text.StartsWith(EnrollmentPayloadUriPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var encodedCode = text[EnrollmentPayloadUriPrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(encodedCode))
            return true;

        try
        {
            enrollmentCode = NormalizeEnrollmentCode(Uri.UnescapeDataString(encodedCode));
        }
        catch
        {
            enrollmentCode = null;
        }

        return true;
    }

    private static bool TryExtractEnrollmentUriQuery(string text, out string? enrollmentCode)
    {
        enrollmentCode = null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "passwordmanagerlocal", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "enrollment", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equalsIndex = part.IndexOf('=');
            if (equalsIndex <= 0)
                continue;

            var key = Uri.UnescapeDataString(part[..equalsIndex]);
            if (!string.Equals(key, "code", StringComparison.OrdinalIgnoreCase))
                continue;

            enrollmentCode = NormalizeEnrollmentCode(Uri.UnescapeDataString(part[(equalsIndex + 1)..]));
            break;
        }

        return true;
    }

    private static string? NormalizeEnrollmentCode(string value)
    {
        var enrollmentCode = value.Trim();
        return string.IsNullOrWhiteSpace(enrollmentCode) ? null : enrollmentCode;
    }



    private static bool LooksLikeEnrollmentCode(string text)
    {
        var normalized = new string(text.Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
        return normalized.StartsWith("PML-", StringComparison.Ordinal)
            || normalized.StartsWith("PML2-", StringComparison.Ordinal)
            || normalized.StartsWith("PML3-", StringComparison.Ordinal);
    }



    private static Bitmap ToAvaloniaBitmap(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = data.AsStream();
        return new Bitmap(stream);
    }
}
