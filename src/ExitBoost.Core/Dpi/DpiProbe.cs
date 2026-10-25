using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace ExitBoost.Core.Dpi;

/// <summary>Замер доступности цели: TCP-соединение и TLS-рукопожатие.</summary>
public static class DpiProbe
{
    /// <summary>
    /// Проверить только TCP-соединение, без TLS. Нужно для игровых серверов:
    /// у них нет сертификата и SNI, а блокировка видна именно по установке
    /// соединения (RST, таймаут).
    /// </summary>
    public static async Task<DpiProbeResult> MeasureTcpAsync(
        string host, int port = 443, int timeoutMs = 6000, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) return DpiProbeResult.Failed("не указан адрес");

        using var client = new TcpClient();
        var sw = Stopwatch.StartNew();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return DpiProbeResult.Failed(ex is OperationCanceledException ? "таймаут" : ex.Message);
        }

        // TLS у игрового сервера нет: успешный TCP-коннект и есть результат.
        return new DpiProbeResult(true, sw.Elapsed.TotalMilliseconds, true, 0, null);
    }

    /// <summary>
    /// Выполнить замер. TLS-рукопожатие — то, что ломает DPI при RST-инъекции,
    /// поэтому именно оно является показателем работоспособности обхода.
    /// </summary>
    public static async Task<DpiProbeResult> MeasureAsync(
        string host, int port = 443, int timeoutMs = 6000, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) return DpiProbeResult.Failed("не указан домен");

        using var client = new TcpClient();
        var sw = Stopwatch.StartNew();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return DpiProbeResult.Failed(ex is OperationCanceledException ? "таймаут" : ex.Message);
        }
        double tcpMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
                (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new DpiProbeResult(true, tcpMs, false, 0, ex is OperationCanceledException ? "таймаут TLS" : ex.Message);
        }

        return new DpiProbeResult(true, tcpMs, true, sw.Elapsed.TotalMilliseconds, null);
    }
}
