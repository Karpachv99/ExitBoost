using System.Diagnostics;
using ExitBoost.Core.Models;

namespace ExitBoost.Core.Tunnel;

/// <summary>
/// SSH-бэкенд: поднимает локальный SOCKS5-прокси на удалённом сервере.
/// Поддержка ограниченная — через прокси работает только софт с поддержкой SOCKS.
/// </summary>
public sealed class SshProxyBackend : ITunnelBackend
{
    private readonly string? _exeOverride;
    private Process? _process;

    public SshProxyBackend(string? exeOverride = null) => _exeOverride = exeOverride;

    public string Name => "SSH SOCKS5";

    public bool IsAvailable => FindExecutable() is not null;

    public string? ErrorMessage => IsAvailable ? null : "Не найден ssh.exe (нужен OpenSSH Client).";

    public TunnelState State { get; private set; } = TunnelState.Disconnected;

    public string? InterfaceAlias => null;

    public IReadOnlyList<string> InterfaceAddresses => Array.Empty<string>();

    public event Action<string>? Log;

    public string? FindExecutable()
    {
        if (!string.IsNullOrWhiteSpace(_exeOverride) && File.Exists(_exeOverride)) return _exeOverride;
        string ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
        if (File.Exists(ssh)) return ssh;
        return null;
    }

    public async Task<TunnelResult> ConnectAsync(NodeConfig node, TunnelOptions options, CancellationToken ct = default)
    {
        var exe = FindExecutable();
        if (exe is null) return TunnelResult.Fail("Не найден ssh.exe");

        if (string.IsNullOrWhiteSpace(node.Username))
            return TunnelResult.Fail("Для SSH-узла не указан пользователь");

        State = TunnelState.Connecting;
        try
        {
            var args = new List<string>
            {
                "-N",                                   // без команды
                "-D", $"127.0.0.1:{node.SocksPort}",      // динамический SOCKS5
                "-p", node.Port.ToString(),
                "-o", "StrictHostKeyChecking=no",
                "-o", "ServerAliveInterval=30",
                "-o", "ExitOnForwardFailure=yes"
            };

            if (!string.IsNullOrWhiteSpace(node.SshKeyPath) && File.Exists(node.SshKeyPath))
            {
                args.Add("-i");
                args.Add($"\"{node.SshKeyPath}\"");
            }

            args.Add($"{node.Username}@{node.Host}");

            string log = "";
            _process = ProcessRunner.StartStreaming(exe, string.Join(' ', args), line =>
            {
                log = (log.Length > 4000 ? log[^4000..] : log) + line + "\n";
            });

            await Task.Delay(1200, ct).ConfigureAwait(false);

            if (_process.HasExited)
            {
                State = TunnelState.Error;
                await DisconnectAsync().ConfigureAwait(false);
                return TunnelResult.Fail("SSH-соединение не установлено. " + log.Trim());
            }
            State = TunnelState.Connected;
            Log?.Invoke($"SOCKS5-прокси поднят на 127.0.0.1:{node.SocksPort}");
            return TunnelResult.Ok($"SOCKS5-прокси: 127.0.0.1:{node.SocksPort}", null, options.AllowedIps);
        }
        catch (Exception ex)
        {
            State = TunnelState.Error;
            return TunnelResult.Fail(ex.Message);
        }
    }

    public Task DisconnectAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(true);
                _process.WaitForExit(5000);
            }
        }
        catch { }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
        State = TunnelState.Disconnected;
        return Task.CompletedTask;
    }

    public void Dispose() => DisconnectAsync().GetAwaiter().GetResult();
}
