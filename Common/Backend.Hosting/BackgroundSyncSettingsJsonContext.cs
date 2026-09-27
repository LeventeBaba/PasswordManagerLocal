using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using System.Text.Json.Serialization;

namespace PasswordManagerLocal.Common.Backend.Hosting;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(BackgroundSyncSettings))]
internal partial class BackgroundSyncSettingsJsonContext : JsonSerializerContext;
