using System.Text.Json.Serialization;

namespace PasswordManagerLocal.Common.Frontend.Services;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(WindowsFirewallConfiguration))]
internal partial class WindowsFirewallConfigurationJsonContext : JsonSerializerContext;
