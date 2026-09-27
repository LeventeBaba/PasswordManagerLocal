using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Concurrent;

namespace PasswordManagerLocal.Common.Backend.Diagnostics;

public static class BackendDebugLog
{
#if DEBUG
    private static readonly object Gate = new();
    private static string? _logPath;
    private static bool _globalExceptionHandlersRegistered;
    private static readonly ConcurrentDictionary<string, DateTimeOffset> RateLimitedDebugWrites = new(StringComparer.Ordinal);
#endif

    [Conditional("DEBUG")]
    public static void InitializeForCurrentBuild(string logsDirectory)
    {
#if DEBUG
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
            var startedAt = DateTimeOffset.Now;
            var fullDirectory = Directory.CreateDirectory(Path.GetFullPath(logsDirectory)).FullName;
            var logPath = CreateUniqueLogPath(fullDirectory, startedAt);

            lock (Gate)
            {
                Volatile.Write(ref _logPath, logPath);
                RegisterGlobalExceptionHandlersLocked();
            }

            var assembly = typeof(BackendDebugLog).Assembly.GetName();
            Info(
                $"Debug backend logging initialized. StartTime={startedAt:O}, " +
                $"ProcessId={Environment.ProcessId}, ProcessArchitecture={RuntimeInformation.ProcessArchitecture}, " +
                $"OS={Environment.OSVersion}, BackendAssembly={assembly.Name}, Version={assembly.Version}, " +
                $"LogPath={logPath}",
                "Logging");
        }
        catch
        {
            // Debug diagnostics must never prevent the backend from starting.
        }
#endif
    }

    [Conditional("DEBUG")]
    public static void Debug(string message, string category = "Backend") =>
        Write("DEBUG", category, message, null);

    [Conditional("DEBUG")]
    public static void DebugRateLimited(
        string key,
        TimeSpan minimumInterval,
        string message,
        string category = "Backend")
    {
#if DEBUG
        if (string.IsNullOrWhiteSpace(key) || minimumInterval <= TimeSpan.Zero)
        {
            Write("DEBUG", category, message, null);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        while (true)
        {
            if (!RateLimitedDebugWrites.TryGetValue(key, out var previous))
            {
                if (RateLimitedDebugWrites.TryAdd(key, now))
                    break;
                continue;
            }

            if (now - previous < minimumInterval)
                return;

            if (RateLimitedDebugWrites.TryUpdate(key, now, previous))
                break;
        }

        Write("DEBUG", category, message, null);
#endif
    }

    [Conditional("DEBUG")]
    public static void Info(string message, string category = "Backend") =>
        Write("INFO", category, message, null);

    [Conditional("DEBUG")]
    public static void Warning(string message, Exception? exception = null, string category = "Backend") =>
        Write("WARN", category, message, exception);

    [Conditional("DEBUG")]
    public static void Error(string message, Exception? exception = null, string category = "Backend") =>
        Write("ERROR", category, message, exception);

    [Conditional("DEBUG")]
    public static void OperationStarted(string operationName, string category = "Operation") =>
        Write("INFO", category, $"{operationName} started.", null);

    [Conditional("DEBUG")]
    public static void OperationCompleted(string operationName, string category = "Operation") =>
        Write("INFO", category, $"{operationName} completed successfully.", null);

    private static void Write(string level, string category, string message, Exception? exception)
    {
#if DEBUG
        try
        {
            var path = Volatile.Read(ref _logPath);
            if (string.IsNullOrWhiteSpace(path))
                return;

            var timestamp = DateTimeOffset.Now;
            var builder = new StringBuilder();
            builder.Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
            builder.Append(" [");
            builder.Append(level);
            builder.Append("] [Thread ");
            builder.Append(Environment.CurrentManagedThreadId);
            builder.Append("] [");
            builder.Append(string.IsNullOrWhiteSpace(category) ? "Backend" : category.Trim());
            builder.Append("] ");
            builder.AppendLine(message);

            if (exception is not null)
            {
                builder.AppendLine("Exception details:");
                builder.AppendLine(exception.ToString());
            }

            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never alter application behavior.
        }
#endif
    }

#if DEBUG
    private static string CreateUniqueLogPath(string logsDirectory, DateTimeOffset startedAt)
    {
        var baseName = startedAt.ToString("yyyy-MM-dd_HH-mm-ss.fff", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(logsDirectory, $"{baseName}.log");
        if (!File.Exists(candidate))
            return candidate;

        for (var index = 1; index < int.MaxValue; index++)
        {
            candidate = Path.Combine(logsDirectory, $"{baseName}_{index:D2}.log");
            if (!File.Exists(candidate))
                return candidate;
        }

        throw new IOException("A unique backend debug log file name could not be allocated.");
    }

    private static void RegisterGlobalExceptionHandlersLocked()
    {
        if (_globalExceptionHandlersRegistered)
            return;

        AppDomain.CurrentDomain.UnhandledException += HandleUnhandledException;
        TaskScheduler.UnobservedTaskException += HandleUnobservedTaskException;
        _globalExceptionHandlersRegistered = true;
    }

    private static void HandleUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        var exception = args.ExceptionObject as Exception;
        Error(
            $"An unhandled exception reached the AppDomain boundary. IsTerminating={args.IsTerminating}.",
            exception,
            "UnhandledException");
    }

    private static void HandleUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        Error(
            "An unobserved task exception was raised.",
            args.Exception,
            "UnobservedTaskException");
    }
#endif
}
