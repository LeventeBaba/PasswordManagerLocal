using Avalonia.Platform;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PasswordManagerLocal.Common.Frontend.Localization;

public static class LocalizationManager
{
    private static readonly object Lock = new();
    private static readonly Dictionary<AppLanguage, Dictionary<string, string>> Translations = new();
    private static readonly HashSet<AppLanguage> LoadAttempts = new();

    public static string GetString(AppLanguage language, string key)
    {
        var languageTranslations = GetOrLoadLanguage(language);
        if (languageTranslations is not null && languageTranslations.TryGetValue(key, out var value))
            return value;

        if (language != AppLanguage.English)
        {
            var englishTranslations = GetOrLoadLanguage(AppLanguage.English);
            if (englishTranslations is not null && englishTranslations.TryGetValue(key, out var englishValue))
                return englishValue;
        }

        return key;
    }

    private static Dictionary<string, string>? GetOrLoadLanguage(AppLanguage language)
    {
        lock (Lock)
        {
            if (Translations.TryGetValue(language, out var existingTranslations))
                return existingTranslations;

            if (!LoadAttempts.Add(language))
                return null;

            var loadedTranslations = LoadLanguage(language);
            if (loadedTranslations is not null)
                Translations[language] = loadedTranslations;

            return loadedTranslations;
        }
    }

    private static Dictionary<string, string>? LoadLanguage(AppLanguage language)
    {
        try
        {
            var assetUri = new Uri(language switch
            {
                AppLanguage.Hungarian => "avares://PasswordManagerLocal.Common.Frontend/Assets/Localization/hu.json",
                _ => "avares://PasswordManagerLocal.Common.Frontend/Assets/Localization/en_us.json"
            });

            if (!AssetLoader.Exists(assetUri))
                return null;

            using var stream = AssetLoader.Open(assetUri);
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                leaveOpen: false);

            return JsonSerializer.Deserialize(
                reader.ReadToEnd(),
                LocalizationJsonContext.Default.TranslationDictionary);
        }
        catch
        {
            return null;
        }
    }
}
