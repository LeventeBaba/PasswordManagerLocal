using System.Diagnostics;

namespace PasswordManagerLocal.Windows.Frontend.Diagnostics;

internal static class WindowsFrontendDebugLog
{
    [Conditional("DEBUG")]
    public static void Info(string message) =>
        Debug.WriteLine($"[INFO] [WindowsFrontend] {message}");

    [Conditional("DEBUG")]
    public static void Error(string message) =>
        Debug.WriteLine($"[ERROR] [WindowsFrontend] {message}");
}
