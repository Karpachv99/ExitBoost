using System.Diagnostics;
using System.Text;

namespace ExitBoost.Core.Routing;

/// <summary>Результат выполнения PowerShell-скрипта.</summary>
public sealed record PsResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;
    public string Combined => string.IsNullOrWhiteSpace(Error) ? Output : $"{Output}\n{Error}".Trim();
}

/// <summary>
/// Выполнение PowerShell-скриптов для управления маршрутами и брандмауэром.
/// Скрипты пишутся во временный файл — это снимает проблемы экранирования кавычек.
/// </summary>
public static class PowerShellRunner
{
    public static async Task<PsResult> RunAsync(string script, int timeoutMs = 60_000, CancellationToken ct = default)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"exitboost_{Guid.NewGuid():N}.ps1");
        try
        {
            // BOM обязателен: PowerShell 5.1 без него читает .ps1 в однобайтовой кодировке
            // и ломает скрипты с не-ASCII символами.
            await File.WriteAllTextAsync(tempFile, script, new UTF8Encoding(true), ct).ConfigureAwait(false);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempFile}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var proc = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                // Синхронное ожидание гарантирует, что асинхронные обработчики
                // вывода успели забрать все данные из перенаправленных потоков.
                proc.WaitForExit();
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                return new PsResult(-1, stdout.ToString(), "Превышено время выполнения скрипта");
            }

            return new PsResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    /// <summary>Выполнить и вернуть только stdout.</summary>
    public static async Task<string> RunTextAsync(string script, int timeoutMs = 60_000, CancellationToken ct = default)
    {
        var r = await RunAsync(script, timeoutMs, ct).ConfigureAwait(false);
        return r.Combined;
    }
}
