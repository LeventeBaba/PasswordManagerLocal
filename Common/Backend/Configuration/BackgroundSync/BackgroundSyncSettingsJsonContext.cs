using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using System.Text.Json.Serialization;

namespace PasswordManagerLocal.Common.Backend.Configuration.BackgroundSync;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(BackgroundSyncSettings))]
internal partial class BackgroundSyncSettingsJsonContext : JsonSerializerContext;
