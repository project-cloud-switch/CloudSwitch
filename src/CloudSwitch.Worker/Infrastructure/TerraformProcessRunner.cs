using System.Diagnostics;
using System.Text;

namespace CloudSwitch.Worker.Infrastructure;

public sealed record ProcessExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool TimedOut,
    bool Cancelled);

public interface ITerraformProcessRunner
{
    Task<ProcessExecutionResult> RunAsync(
        string executable,
        string arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

public sealed class TerraformProcessRunner : ITerraformProcessRunner
{
    public async Task<ProcessExecutionResult> RunAsync(
        string executable,
        string arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (environmentVariables is not null)
        {
            foreach (var (k, v) in environmentVariables)
                startInfo.Environment[k] = v;
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stdout.AppendLine(OutputSanitizer.Sanitize(e.Data));
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stderr.AppendLine(OutputSanitizer.Sanitize(e.Data));
        };

        var stopwatch = Stopwatch.StartNew();

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            return new ProcessExecutionResult(
                ExitCode: -1,
                StandardOutput: "",
                StandardError: OutputSanitizer.Sanitize($"Failed to start process '{executable}': {ex.Message}"),
                Duration: stopwatch.Elapsed,
                TimedOut: false,
                Cancelled: false);
        }

        var processTimeout = timeout ?? TimeSpan.FromMinutes(5);
        using var timeoutCts = new CancellationTokenSource(processTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
            stopwatch.Stop();
            return new ProcessExecutionResult(
                ExitCode: process.ExitCode,
                StandardOutput: stdout.ToString(),
                StandardError: stderr.ToString(),
                Duration: stopwatch.Elapsed,
                TimedOut: false,
                Cancelled: false);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            var isTimeout = timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;

            try
            {
                // Terminate the entire process tree to guarantee no orphaned terraform or provider processes
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Process may have already exited
            }

            return new ProcessExecutionResult(
                ExitCode: -1,
                StandardOutput: stdout.ToString(),
                StandardError: OutputSanitizer.Sanitize(isTimeout
                    ? $"Process timed out after {processTimeout.TotalSeconds:F0}s."
                    : "Process execution cancelled."),
                Duration: stopwatch.Elapsed,
                TimedOut: isTimeout,
                Cancelled: !isTimeout);
        }
    }
}
