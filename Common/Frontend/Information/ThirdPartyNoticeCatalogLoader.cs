using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace PasswordManagerLocal.Common.Frontend.Information;

public static class ThirdPartyNoticeCatalogLoader
{
    private const int SupportedSchemaVersion = 1;
    private const string ManifestResourceName =
        "PasswordManagerLocal.Common.Frontend.Assets.Legal.third-party-notices.json";
    private const string LicenseTextsResourceName =
        "PasswordManagerLocal.Common.Frontend.Assets.Legal.license-texts.json";

    public static ThirdPartyNoticeCatalog Load()
    {
        var assembly = typeof(ThirdPartyNoticeCatalogLoader).Assembly;
        using var manifest = OpenRequiredResource(assembly, ManifestResourceName);
        using var licenseTexts = OpenRequiredResource(assembly, LicenseTextsResourceName);
        using var manifestDocument = JsonDocument.Parse(manifest, StrictDocumentOptions);
        using var licenseTextDocument = JsonDocument.Parse(licenseTexts, StrictDocumentOptions);

        var root = manifestDocument.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The third-party notice manifest must be a JSON object.");

        var schemaVersion = ReadRequiredInt32(root, "schemaVersion");
        if (schemaVersion != SupportedSchemaVersion)
            throw new InvalidDataException("The third-party notice manifest schema is unsupported.");

        var reviewedDateText = ReadRequiredString(root, "reviewedDate");
        if (!DateOnly.TryParseExact(
                reviewedDateText,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var reviewedDate))
        {
            throw new InvalidDataException("The third-party notice review date is invalid.");
        }

        var texts = ReadLicenseTexts(licenseTextDocument.RootElement);
        var entries = root.GetProperty("entries");
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() == 0)
            throw new InvalidDataException("The third-party notice manifest contains no entries.");

        var notices = new List<ThirdPartyNotice>(entries.GetArrayLength());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            var id = ReadRequiredString(entry, "id");
            if (!ids.Add(id))
                throw new InvalidDataException($"The third-party notice id '{id}' is duplicated.");

            var sourceText = ReadRequiredString(entry, "sourceUrl");
            if (!Uri.TryCreate(sourceText, UriKind.Absolute, out var sourceUri) ||
                !sourceUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The third-party notice source for '{id}' must use HTTPS.");
            }

            var licenseTextId = ReadRequiredString(entry, "licenseTextId");
            if (!texts.TryGetValue(licenseTextId, out var licenseText))
            {
                throw new InvalidDataException(
                    $"The third-party notice '{id}' refers to a missing license text.");
            }

            var copyrightNotice = ReadRequiredString(entry, "copyrightNotice");

            notices.Add(new ThirdPartyNotice(
                id,
                ReadRequiredString(entry, "name"),
                ReadRequiredString(entry, "version"),
                ReadRequiredString(entry, "licenseExpression"),
                copyrightNotice,
                sourceUri,
                ReadPlatforms(entry.GetProperty("platforms"), id),
                ReadPackages(entry.GetProperty("packages"), id, sourceUri),
                ReadOptionalString(entry, "requiredNotice"),
                $"{copyrightNotice}{Environment.NewLine}{Environment.NewLine}{licenseText}"));
        }

        return new ThirdPartyNoticeCatalog(
            schemaVersion,
            reviewedDate,
            notices.OrderBy(notice => notice.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static readonly JsonDocumentOptions StrictDocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow
    };

    private static Stream OpenRequiredResource(Assembly assembly, string resourceName) =>
        assembly.GetManifestResourceStream(resourceName) ??
        throw new FileNotFoundException("A bundled third-party notice resource is unavailable.");

    private static Dictionary<string, string> ReadLicenseTexts(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The license-text resource must be a JSON object.");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(property.Value.GetString()) ||
                !result.TryAdd(property.Name, property.Value.GetString()!))
            {
                throw new InvalidDataException("The license-text resource contains an invalid entry.");
            }
        }

        return result;
    }

    private static IReadOnlyList<ThirdPartyPackage> ReadPackages(
        JsonElement packages,
        string id,
        Uri defaultSourceUri)
    {
        if (packages.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"The package list for '{id}' is invalid.");

        var result = new List<ThirdPartyPackage>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages.EnumerateArray())
        {
            var name = ReadRequiredString(package, "name");
            var version = ReadRequiredString(package, "version");
            if (!names.Add(name))
                throw new InvalidDataException($"The package '{name}' is duplicated in '{id}'.");

            var sourceUri = ReadOptionalSourceUri(package, id, name) ?? defaultSourceUri;
            var packagePageUri = new Uri(
                $"https://www.nuget.org/packages/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(version)}",
                UriKind.Absolute);
            result.Add(new ThirdPartyPackage(name, version, packagePageUri, sourceUri));
        }

        return result;
    }

    private static Uri? ReadOptionalSourceUri(JsonElement package, string noticeId, string packageName)
    {
        var sourceText = ReadOptionalString(package, "sourceUrl");
        if (sourceText is null)
            return null;

        if (!Uri.TryCreate(sourceText, UriKind.Absolute, out var sourceUri) ||
            !sourceUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The source for package '{packageName}' in '{noticeId}' must use HTTPS.");
        }

        return sourceUri;
    }

    private static ThirdPartyPlatform ReadPlatforms(JsonElement platforms, string id)
    {
        if (platforms.ValueKind != JsonValueKind.Array || platforms.GetArrayLength() == 0)
            throw new InvalidDataException($"The platform list for '{id}' is invalid.");

        var result = ThirdPartyPlatform.None;
        foreach (var platform in platforms.EnumerateArray())
        {
            result |= platform.GetString() switch
            {
                "Windows" => ThirdPartyPlatform.Windows,
                "Android" => ThirdPartyPlatform.Android,
                _ => throw new InvalidDataException(
                    $"The platform list for '{id}' contains an unsupported value.")
            };
        }

        return result;
    }

    private static string ReadRequiredString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException(
                $"The third-party notice property '{propertyName}' is missing or empty.");
        }

        return property.GetString()!.Trim();
    }

    private static string? ReadOptionalString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"The third-party notice property '{propertyName}' is invalid.");

        var value = property.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int ReadRequiredInt32(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var property) ||
            !property.TryGetInt32(out var value))
        {
            throw new InvalidDataException(
                $"The third-party notice property '{propertyName}' is missing or invalid.");
        }

        return value;
    }
}
