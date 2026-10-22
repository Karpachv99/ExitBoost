using System.Diagnostics;
using System.Text;

namespace ExitBoost.Core.Updates;

/// <summary>
/// Применение обновления без отдельного установщика: готовим скрипт, который
/// дождётся выхода приложения, распакует архив релиза поверх папки программы
/// и запустит её заново.
/// </summary>
public static class UpdateApplier
{
    /// <summary>Куда пишется журнал обновления (если что-то пойдёт не так).</summary>
    public static string LogPath { get; } = Path.Combine(Path.GetTempPath(), "ExitBoost-update.log");

    /// <summary>
    /// Создать и запустить скрипт обновления. Вызывать непосредственно перед закрытием приложения:
    /// скрипт ждёт завершения процесса <paramref name="processId"/>, затем распаковывает архив.
    /// </summary>
    public static string ScheduleApply(string zipPath, string installDirectory, string exePath, int processId)
    {
        string script = Path.Combine(Path.GetTempPath(), $"ExitBoost-update-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(script, BuildScript(zipPath, installDirectory, exePath, processId), new UTF8Encoding(true));

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath()
        };
        Process.Start(startInfo);
        return script;
    }

    /// <summary>
    /// Текст скрипта. Служебные сообщения — только латиницей: Windows PowerShell 5.1
    /// читает .ps1 в системной кодировке, и кириллица может превратиться в мусор.
    /// </summary>
    public static string BuildScript(string zipPath, string installDirectory, string exePath, int processId)
    {
        string zip = Quote(zipPath);
        string dir = Quote(installDirectory);
        string exe = Quote(exePath);
        string log = Quote(LogPath);

        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine($"$log = {log}");
        sb.AppendLine("function Note([string]$text) {");
        sb.AppendLine("  try { ('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $text) | Out-File -FilePath $log -Append -Encoding UTF8 } catch { }");
        sb.AppendLine("}");
        sb.AppendLine($"Note 'update: waiting for process {processId} to exit'");
        sb.AppendLine("try {");
        sb.AppendLine($"  $process = Get-Process -Id {processId} -ErrorAction Stop");
        sb.AppendLine("  $process.WaitForExit(120000) | Out-Null");
        sb.AppendLine("} catch { }");
        sb.AppendLine("Start-Sleep -Milliseconds 900");
        sb.AppendLine("try {");
        sb.AppendLine("  Add-Type -AssemblyName System.IO.Compression.FileSystem");
        sb.AppendLine($"  $zip = [System.IO.Compression.ZipFile]::OpenRead({zip})");
        sb.AppendLine($"  $root = [System.IO.Path]::GetFullPath({dir})");
        sb.AppendLine("  $failed = 0");
        sb.AppendLine("  foreach ($entry in $zip.Entries) {");
        sb.AppendLine("    try {");
        sb.AppendLine("      $target = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($root, $entry.FullName))");
        sb.AppendLine("      if (-not $target.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) { continue }");
        sb.AppendLine("      if ([string]::IsNullOrEmpty($entry.Name)) {");
        sb.AppendLine("        if (-not (Test-Path $target)) { New-Item -ItemType Directory -Path $target -Force | Out-Null }");
        sb.AppendLine("        continue");
        sb.AppendLine("      }");
        sb.AppendLine("      $folder = Split-Path -Path $target -Parent");
        sb.AppendLine("      if (-not (Test-Path $folder)) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }");
        sb.AppendLine("      [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)");
        sb.AppendLine("    } catch {");
        sb.AppendLine("      $failed++");
        sb.AppendLine("      Note ('update: failed to write ' + $entry.FullName + ': ' + $_.Exception.Message)");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("  $zip.Dispose()");
        sb.AppendLine("  Note ('update: files extracted, write errors: ' + $failed)");
        sb.AppendLine("} catch {");
        sb.AppendLine("  Note ('update: extraction failed - ' + $_.Exception.Message)");
        sb.AppendLine("}");
        sb.AppendLine("Start-Sleep -Milliseconds 300");
        sb.AppendLine($"Start-Process -FilePath {exe} -WorkingDirectory {dir}");
        sb.AppendLine("Note 'update: app started again'");
        return sb.ToString();
    }

    /// <summary>Строка в одинарных кавычках PowerShell (кавычки внутри удваиваются).</summary>
    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}
