using System.Diagnostics;
using System.Text;

namespace ExitBoost.Core.Tunnel;

public sealed record ProcessRunResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;
    public string Combined => string.IsNullOrWhiteSpace(Error) ? Output : $"{Output}\n{Error}";
}

/// <summary>Запуск внешних процессов с захватом вывода.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessRunResult> RunAsync(
        string fileName,
        string arguments,
        int timeoutMs = 30_000,
        CancellationToken ct = default,
        string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory)) psi.WorkingDirectory = workingDirectory;

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try { proc.Start(); }
        catch (Exception ex) { return new ProcessRunResult(-1, "", ex.Message); }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            // Гарантируем, что обработчики вывода забрали все данные.
            proc.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            return new ProcessRunResult(-1, stdout.ToString(), "Таймаут выполнения");
        }

        return new ProcessRunResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Запустить длительно живущий процесс (openvpn, ssh) с колбэком на вывод.</summary>
    public static Process StartStreaming(
        string fileName,
        string arguments,
        Action<string>? onOutput = null,
        string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory)) psi.WorkingDirectory = workingDirectory;

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (onOutput is not null)
        {
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutput(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) onOutput(e.Data); };
        }

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return proc;
    }

    /// <summary>
    /// Запустить длительно живущий процесс в отдельном скрытом окне.
    /// Такой процесс не наследует консоль родителя: он переживает выход
    /// консольной программы и не держит её каналы вывода открытыми.
    /// </summary>
    public static Process StartDetached(
        string fileName,
        string arguments,
        string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory)) psi.WorkingDirectory = workingDirectory;

        return Process.Start(psi) ?? throw new InvalidOperationException($"Не удалось запустить {fileName}");
    }
}
