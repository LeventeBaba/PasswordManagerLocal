using System.Diagnostics;
using System.Text;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class ProcessTestHostClient : IAsyncDisposable
{
    private static readonly TimeSpan LineTimeout = TimeSpan.FromSeconds(5);
    private readonly Process _process;
    private readonly StringBuilder _standardError = new();
    private readonly object _errorGate = new();
    private readonly Task _errorPump;
    private int _disposed;

    private ProcessTestHostClient(Process process)
    {
        _process = process;
        _errorPump = PumpErrorsAsync();
    }

    public int ProcessId => _process.Id;
    public bool IsRunning => !_process.HasExited;
    public string Diagnostics
    {
        get
        {
            lock (_errorGate)
            {
                return $"PID={_process.Id}; HasExited={_process.HasExited}; ExitCode={TryGetExitCode()}; STDERR={_standardError}";
            }
        }
    }

    public static async Task<ProcessTestHostClient> StartAsync(
        string mode,
        string lockPath,
        string expectedReadyPrefix = "READY")
    {
        var executablePath = Path.Combine(
            AppContext.BaseDirectory,
            "TestHosts",
            "ProcessLockHost",
            "PasswordManagerLocal.Windows.Tests.Host.exe");
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("The Windows process test host was not copied to the test output.", executablePath);

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add(lockPath);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Windows process test host did not start.");
        var client = new ProcessTestHostClient(process);
        try
        {
            var line = await client.ReadLineAsync();
            if (!line.StartsWith(expectedReadyPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Unexpected test-host readiness response: {line}. {client.Diagnostics}");
            if (expectedReadyPrefix == "NOT_OWNER")
                await process.WaitForExitAsync().WaitAsync(LineTimeout);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task<string> ReadLineAsync()
    {
        var line = await _process.StandardOutput.ReadLineAsync().WaitAsync(LineTimeout);
        return line ?? throw new EndOfStreamException($"The process test host closed stdout. {Diagnostics}");
    }

    public async Task SendAsync(string command)
    {
        await _process.StandardInput.WriteLineAsync(command);
        await _process.StandardInput.FlushAsync();
    }

    public async Task TerminateAsync()
    {
        if (!_process.HasExited)
            _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync().WaitAsync(LineTimeout);
        await _errorPump.WaitAsync(LineTimeout);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            await TerminateAsync();
        }
        finally
        {
            _process.Dispose();
        }
    }

    private async Task PumpErrorsAsync()
    {
        while (await _process.StandardError.ReadLineAsync() is { } line)
        {
            lock (_errorGate)
                _standardError.AppendLine(line);
        }
    }

    private string TryGetExitCode()
    {
        try { return _process.HasExited ? _process.ExitCode.ToString() : "running"; }
        catch { return "unavailable"; }
    }
}
