using PasswordManagerLocal.Windows.Ipc.Coordination;

namespace PasswordManagerLocal.Windows.Tests.Host;

internal sealed class Program
{
    private const string HoldMode = "hold-lock";
    private const string FailedDisposeMode = "failed-dispose-lock";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 ||
            (args[0] != HoldMode && args[0] != FailedDisposeMode) ||
            !Path.IsPathFullyQualified(args[1]))
        {
            Console.Error.WriteLine("Usage: hold-lock|failed-dispose-lock <absolute-lock-path>");
            return 2;
        }

        var opener = args[0] == FailedDisposeMode
            ? new RetainingThrowingLockFileOpener()
            : null;
        using var processLock = new FileProcessInstanceLock(args[1], opener);
        if (!processLock.IsOwner)
        {
            Console.WriteLine("NOT_OWNER");
            return 3;
        }

        Console.WriteLine($"READY {Environment.ProcessId}");
        Console.Out.Flush();

        var command = await Console.In.ReadLineAsync();
        if (args[0] == FailedDisposeMode && command == "dispose")
        {
            try
            {
                processLock.Dispose();
                Console.WriteLine("UNEXPECTED_DISPOSE_SUCCESS");
                return 4;
            }
            catch (IOException)
            {
                Console.WriteLine("DISPOSE_FAILED");
                Console.Out.Flush();
                await Console.In.ReadLineAsync();
                return 0;
            }
        }

        return command == "exit" ? 0 : 5;
    }
}
