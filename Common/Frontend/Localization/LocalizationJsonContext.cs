using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PasswordManagerLocal.Common.Frontend.Localization;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(
    typeof(Dictionary<string, string>),
    TypeInfoPropertyName = "TranslationDictionary")]
internal partial class LocalizationJsonContext : JsonSerializerContext;
