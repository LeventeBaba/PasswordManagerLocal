using System.Text.Json.Serialization;

using PasswordManagerLocal.Common.Tests.TestInfrastructure.Serialization;
namespace PasswordManagerLocal.Common.Tests.Backend.Utils;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(CodecPayload), TypeInfoPropertyName = "CodecPayload")]
internal partial class DataCodecTestJsonContext : JsonSerializerContext;
