using PasswordManagerLocal.Common.Contracts.Preferences;
using System.Text.Json.Serialization;

namespace PasswordManagerLocal.Common.Preferences;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ApplicationPreferences))]
internal partial class ApplicationPreferencesJsonContext : JsonSerializerContext;
