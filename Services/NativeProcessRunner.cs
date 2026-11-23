using System.Diagnostics;

namespace HdrImageViewer.Services;

/// <summary>
/// Shared helpers for spawning the bundled native CLI encoders/decoders
/// (cjxl / djxl / jxlinfo / avifenc / heif-enc / heif-dec / ultrahdr_app, ...).
/// </summary>
internal static class NativeProcessRunner
{
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

    public static async Task<string> RunAsync(Process process, string backendName, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        if (!process.Start())
        {
            throw new InvalidOperationException($"启动 {backendName} 失败。");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            try
            {
                await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
            }
            catch (TimeoutException)
            {
            }
            catch (IOException)
            {
            }

            if (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                throw new TimeoutException($"{backendName} 超时，已停止编码/解码进程。");
            }

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
        }
    }

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
