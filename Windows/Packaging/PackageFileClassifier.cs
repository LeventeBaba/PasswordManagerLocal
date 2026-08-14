namespace PasswordManagerLocal.Windows.Packaging;

internal static class PackageFileClassifier
{
    private static readonly HashSet<string> NativeRuntimeFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "clretwrc.dll", "clrgc.dll", "clrgcexp.dll", "clrjit.dll", "coreclr.dll",
        "hostfxr.dll", "hostpolicy.dll", "mscorrc.dll", "msquic.dll",
        "System.IO.Compression.Native.dll"
    };

    public static string Classify(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        if (fileName.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase))
            return "dependency-manifest";
        if (fileName.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
            return "runtime-config";
        if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return "executable";
        if (relativePath.Contains("Localization", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
            return "localization";
        if (NativeRuntimeFiles.Contains(fileName))
            return "runtime-native";
        if (fileName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("Microsoft.Win32.", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase))
            return "runtime-managed";
        if (fileName.StartsWith("PasswordManagerLocal.", StringComparison.OrdinalIgnoreCase))
            return "application-managed";
        if (fileName.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("Skia", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("HarfBuzz", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Reactive", StringComparison.OrdinalIgnoreCase))
            return "frontend-asset";
        if (fileName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("sqlcipher", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("sodium", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase))
            return "agent-backend-asset";
        if (fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return "managed-or-native-library";
        return "asset";
    }
}
