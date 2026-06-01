using System.Diagnostics;

namespace HdrImageViewer.Services;

/// <summary>
/// Shared helpers for spawning the bundled native CLI encoders/decoders
/// (cjxl / djxl / jxlinfo / avifenc / heif-enc / heif-dec / ultrahdr_app, ...).
///
/// Previously each service (SingleLayerHdrExportService, JxlProbe,
/// GainMapHdrExportService, BitmapDecodeService) carried its own near-identical
/// copy of this logic, which meant correctness fixes such as "kill the child
/// process tree on cancellation so the spawned CLI does not leak and hold file
/// handles" had to be applied in every copy independently. Centralising it here
/// keeps that behaviour in one place.
/// </summary>
internal static class NativeProcessRunner
{
    /// <summary>
    /// Creates a <see cref="Process"/> configured to run a native CLI with no
    /// window and with stdout/stderr redirected so the caller can capture them.
    /// </summary>
    public static Process Create(string executablePath)
    {
        var process = new Process();
        process.StartInfo.FileName = executablePath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        return process;
    }

    /// <summary>
    /// Starts the process, drains stdout/stderr to completion, and waits for
    /// exit. On cancellation the entire child process tree is killed so the
    /// spawned CLI cannot keep running orphaned. Throws
    /// <see cref="InvalidOperationException"/> on a non-zero exit code.
    /// </summary>
    /// <returns>The combined, trimmed stdout/stderr text.</returns>
    public static async Task<string> RunAsync(Process process, string backendName, CancellationToken cancellationToken)
    {
        if (!process.Start())
        {
            throw new InvalidOperationException($"启动 {backendName} 失败。");
        }

        // Drain the pipes with CancellationToken.None so the reads finish at EOF
        // instead of surfacing as unobserved cancellations after we kill the tree.
        var outputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
        }

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            var message = string.Join("\n", new[] { output.Trim(), error.Trim() }.Where(part => !string.IsNullOrWhiteSpace(part)));
            throw new InvalidOperationException($"{backendName} 失败，exit {process.ExitCode}: {message}");
        }

        return string.Join("\n", new[] { output.Trim(), error.Trim() }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort: the process may have already exited or be inaccessible.
        }
    }

    /// <summary>
    /// Searches the directories on the PATH environment variable for an
    /// executable and returns its full path, or <c>null</c> if not found.
    /// Inaccessible directories are skipped.
    /// </summary>
    public static string? FindExecutableOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                continue;
            }
        }

        return null;
    }
}
