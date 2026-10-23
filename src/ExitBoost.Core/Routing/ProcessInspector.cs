using System.Diagnostics;
using ExitBoost.Core.Models;

namespace ExitBoost.Core.Routing;

/// <summary>Список запущенных процессов, пригодных в качестве цели (игра).</summary>
public static class ProcessInspector
{
    /// <summary>Все процессы с доступным исполняемым файлом.</summary>
    public static List<SelectedProcess> GetProcesses(bool onlyWithWindow = false)
    {
        var list = new List<SelectedProcess>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string title = p.MainWindowTitle ?? "";
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { }
                if (onlyWithWindow && string.IsNullOrWhiteSpace(title)) continue;
                if (string.IsNullOrWhiteSpace(p.ProcessName)) continue;

                list.Add(new SelectedProcess
                {
                    Pid = p.Id,
                    Name = p.ProcessName,
                    Path = path,
                    WindowTitle = string.IsNullOrWhiteSpace(title) ? null : title
                });
            }
            catch { }
            finally { p.Dispose(); }
        }
        return list
            .OrderByDescending(p => !string.IsNullOrWhiteSpace(p.WindowTitle))
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Найти PID процессов по имени файла.</summary>
    public static List<int> FindPids(string processNameOrPath)
    {
        string name = processNameOrPath;
        if (name.Contains('\\') || name.Contains('/'))
            name = Path.GetFileNameWithoutExtension(name);

        var result = new List<int>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase))
                    result.Add(p.Id);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return result;
    }

    /// <summary>Удалённые адреса указанного процесса (для автоопределения игровых серверов).</summary>
    public static List<string> GetRemoteAddresses(int pid) =>
        ConnectionTable.GetRemoteIpv4ByPid(pid);
}
