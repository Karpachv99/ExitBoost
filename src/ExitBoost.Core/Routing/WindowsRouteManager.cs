using System.Runtime.InteropServices;
using System.Text.Json;

namespace ExitBoost.Core.Routing;

/// <summary>Сетевой интерфейс.</summary>
public sealed record NetAdapterInfo(string Name, string Description, string Status, int IfIndex, string? MacAddress);

/// <summary>Маршрут по умолчанию.</summary>
public sealed record DefaultRouteInfo(string InterfaceAlias, int IfIndex, string NextHop, int RouteMetric, int PrefixLength)
{
    public string Prefix => PrefixLength == 0 ? "0.0.0.0/0" : $"0.0.0.0/{PrefixLength}";
}

/// <summary>Правило брандмауэра, созданное приложением.</summary>
public sealed record FirewallRuleInfo(string Name, string DisplayName, string Action, string Direction, string? Program);

/// <summary>
/// Управление маршрутами и правилами Windows Firewall.
/// Все операции требуют прав администратора.
/// </summary>
public sealed class WindowsRouteManager
{
    /// <summary>Группа правил приложения — по ней выполняется массовая очистка.</summary>
    public const string RuleGroup = "ExitBoost";

    /// <summary>Журнал диагностики (устанавливается приложением).</summary>
    public Action<string>? Log { get; set; }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern bool IsUserAnAdmin();

    /// <summary>Запущен ли процесс с правами администратора.</summary>
    public static bool IsElevated()
    {
        try { return IsUserAnAdmin(); }
        catch { return false; }
    }

    // ------------------------------------------------------------- интерфейсы

    public async Task<IReadOnlyList<NetAdapterInfo>> GetAdaptersAsync(CancellationToken ct = default)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            Get-NetAdapter | ForEach-Object {
                $mac = $null
                try { $mac = $_.MacAddress } catch {}
                [PSCustomObject]@{
                    Name = $_.Name
                    Description = $_.InterfaceDescription
                    Status = $_.Status.ToString()
                    IfIndex = [int]$_.ifIndex
                    MacAddress = $mac
                }
            } | ConvertTo-Json -Compress
            """;

        var result = await PowerShellRunner.RunAsync(script, 30_000, ct).ConfigureAwait(false);
        return ParseJsonArray<NetAdapterInfo>(result.Output);
    }

    /// <summary>Интерфейс, через который уходит интернет (маршрут по умолчанию).</summary>
    public async Task<DefaultRouteInfo?> GetDefaultRouteAsync(CancellationToken ct = default)
    {
        const string script = """
            $ErrorActionPreference = 'SilentlyContinue'
            Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' |
                Sort-Object RouteMetric, ifMetric |
                Select-Object -First 1 |
                ForEach-Object {
                    [PSCustomObject]@{
                        InterfaceAlias = $_.InterfaceAlias
                        IfIndex = [int]$_.ifIndex
                        NextHop = $_.NextHop
                        RouteMetric = [int]$_.RouteMetric
                        PrefixLength = [int]$_.PrefixLength
                    }
                } | ConvertTo-Json -Compress
            """;

        var result = await PowerShellRunner.RunAsync(script, 30_000, ct).ConfigureAwait(false);
        var text = result.Output.Trim();
        if (text.Length == 0)
        {
            Log?.Invoke($"Маршрут по умолчанию не найден. Вывод скрипта: {result.Combined.Trim()}");
            return null;
        }
        try { return JsonSerializer.Deserialize<DefaultRouteInfo>(text, JsonOpts); }
        catch (Exception ex)
        {
            Log?.Invoke($"Не удалось разобрать маршрут по умолчанию: {ex.Message}. Ответ: {text}");
            return null;
        }
    }

    /// <summary>Адреса, назначенные интерфейсу.</summary>
    public async Task<IReadOnlyList<string>> GetInterfaceAddressesAsync(string alias, CancellationToken ct = default)
    {
        string a = EscapePs(alias);
        string script = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            Get-NetIPAddress -AddressFamily IPv4 -InterfaceAlias '{{a}}' |
                ForEach-Object {
                    if ($_.IPAddress -notlike '169.254.*') { $_.IPAddress }
                }
            """;

        var result = await PowerShellRunner.RunAsync(script, 30_000, ct).ConfigureAwait(false);
        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && !s.Contains(' '))
            .Distinct()
            .ToList();
    }

    /// <summary>Найти интерфейс туннеля по описанию/имени.</summary>
    public async Task<NetAdapterInfo?> FindTunnelAdapterAsync(string[] hints, CancellationToken ct = default)
    {
        var adapters = await GetAdaptersAsync(ct).ConfigureAwait(false);
        foreach (var hint in hints)
        {
            var found = adapters.FirstOrDefault(a =>
                a.Description.Contains(hint, StringComparison.OrdinalIgnoreCase) ||
                a.Name.Contains(hint, StringComparison.OrdinalIgnoreCase));
            if (found is not null) return found;
        }
        return null;
    }

    // ---------------------------------------------------------------- маршруты

    /// <summary>Добавить маршрут (префикс) через интерфейс.</summary>
    public async Task<bool> AddRouteAsync(string prefix, string interfaceAlias, int metric = 1, CancellationToken ct = default)
    {
        string p = EscapePs(prefix);
        string a = EscapePs(interfaceAlias);
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            if (-not (Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '{{p}}' -ErrorAction SilentlyContinue |
                       Where-Object { $_.InterfaceAlias -eq '{{a}}' })) {
                New-NetRoute -AddressFamily IPv4 -DestinationPrefix '{{p}}' -InterfaceAlias '{{a}}' -RouteMetric {{metric}} -PolicyStore ActiveStore | Out-Null
            }
            Write-Output 'OK'
            """;
        var r = await PowerShellRunner.RunAsync(script, 30_000, ct).ConfigureAwait(false);
        return r.Ok;
    }

    /// <summary>Удалить маршрут.</summary>
    public async Task<bool> RemoveRouteAsync(string prefix, string interfaceAlias, CancellationToken ct = default)
    {
        string p = EscapePs(prefix);
        string a = EscapePs(interfaceAlias);
        string script = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '{{p}}' -ErrorAction SilentlyContinue |
                Where-Object { $_.InterfaceAlias -eq '{{a}}' } |
                Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
            Write-Output 'OK'
            """;
        var r = await PowerShellRunner.RunAsync(script, 30_000, ct).ConfigureAwait(false);
        return r.Ok;
    }

    /// <summary>Удалить маршрут по умолчанию с указанного интерфейса (нужно для kill-switch).</summary>
    public async Task<bool> RemoveDefaultRouteAsync(string interfaceAlias, CancellationToken ct = default)
    {
        string a = EscapePs(interfaceAlias);
        string script = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
                Where-Object { $_.InterfaceAlias -eq '{{a}}' -and $_.NextHop -ne '0.0.0.0' } |
                Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
            Write-Output 'OK'
            """;
        var r = await PowerShellRunner.RunAsync(script, 30_000, ct).ConfigureAwait(false);
        return r.Ok;
    }

    /// <summary>Восстановить маршрут по умолчанию.</summary>
    public async Task<bool> RestoreDefaultRouteAsync(DefaultRouteInfo route, CancellationToken ct = default)
    {
        string a = EscapePs(route.InterfaceAlias);
        string script = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            $existing = Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '{{route.Prefix}}' -ErrorAction SilentlyContinue |
                Where-Object { $_.InterfaceAlias -eq '{{a}}' -and $_.NextHop -eq '{{route.NextHop}}' }
            if (-not $existing) {
                New-NetRoute -AddressFamily IPv4 -DestinationPrefix '{{route.Prefix}}' -InterfaceAlias '{{a}}' -NextHop '{{route.NextHop}}' -RouteMetric {{route.RouteMetric}} -PolicyStore ActiveStore | Out-Null
            }
            Write-Output 'OK'
            """;
        var r = await PowerShellRunner.RunAsync(script, 30_000, ct).ConfigureAwait(false);
        return r.Ok;
    }

    /// <summary>Убедиться, что до адреса узла есть маршрут через физический интерфейс.</summary>
    public async Task<bool> EnsureHostRouteAsync(string ip, string interfaceAlias, CancellationToken ct = default)
    {
        string p = EscapePs(ip);
        string a = EscapePs(interfaceAlias);
        string script = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            $r = Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '{{p}}/32' -ErrorAction SilentlyContinue
            if (-not $r) {
                New-NetRoute -AddressFamily IPv4 -DestinationPrefix '{{p}}/32' -InterfaceAlias '{{a}}' -RouteMetric 1 -PolicyStore ActiveStore | Out-Null
            }
            Write-Output 'OK'
            """;
        var r = await PowerShellRunner.RunAsync(script, 30_000, ct).ConfigureAwait(false);
        return r.Ok;
    }

    /// <summary>Активные маршруты для интерфейса.</summary>
    public async Task<IReadOnlyList<string>> GetInterfaceRoutesAsync(string interfaceAlias, CancellationToken ct = default)
    {
        string a = EscapePs(interfaceAlias);
        string script = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            Get-NetRoute -AddressFamily IPv4 -InterfaceAlias '{{a}}' -ErrorAction SilentlyContinue |
                Select-Object -ExpandProperty DestinationPrefix
            """;
        var r = await PowerShellRunner.RunAsync(script, 30_000, ct).ConfigureAwait(false);
        return r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
    }

    // ------------------------------------------------------------ брандмауэр

    /// <summary>
    /// Создать правило исходящего трафика.
    /// Интерфейс указывается в правиле, чтобы блокировка не затрагивала трафик туннеля.
    /// </summary>
    public Task<bool> AddOutboundRuleAsync(
        string name,
        bool allow,
        string? programPath = null,
        string? interfaceAlias = null,
        string? remoteAddress = null,
        string? remotePort = null,
        string protocol = "Any",
        string description = "",
        CancellationToken ct = default) =>
        AddOutboundRuleListAsync(name, allow, programPath, interfaceAlias,
            remoteAddress is null ? null : new[] { remoteAddress }, remotePort, protocol, description, ct);

    /// <summary>Создать правило исходящего трафика с набором удалённых адресов.</summary>
    public async Task<bool> AddOutboundRuleListAsync(
        string name,
        bool allow,
        string? programPath,
        string? interfaceAlias,
        IReadOnlyList<string>? remoteAddresses,
        string? remotePort,
        string protocol,
        string description,
        CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"Remove-NetFirewallRule -Name '{EscapePs(name)}' -ErrorAction SilentlyContinue");
        sb.Append("New-NetFirewallRule -Name ");
        sb.Append($"'{EscapePs(name)}' -DisplayName '{EscapePs(name)}' -Group '{RuleGroup}' ");
        sb.Append(allow ? "-Action Allow " : "-Action Block ");
        sb.Append("-Direction Outbound -Enabled True -Profile Any -Program Any ");
        if (!string.IsNullOrWhiteSpace(programPath))
            sb.Append($"-Program '{EscapePs(programPath)}' ");
        if (!string.IsNullOrWhiteSpace(interfaceAlias))
            sb.Append($"-InterfaceAlias '{EscapePs(interfaceAlias)}' ");
        if (remoteAddresses is { Count: > 0 })
        {
            var list = string.Join(",", remoteAddresses.Select(a => $"'{EscapePs(a)}'"));
            sb.Append($"-RemoteAddress @({list}) ");
        }
        if (!string.IsNullOrWhiteSpace(remotePort))
            sb.Append($"-RemotePort {EscapePs(remotePort)} ");
        if (!string.IsNullOrWhiteSpace(protocol) && protocol != "Any")
            sb.Append($"-Protocol {EscapePs(protocol)} ");
        if (!string.IsNullOrWhiteSpace(description))
            sb.Append($"-Description '{EscapePs(description)}' ");
        sb.AppendLine("| Out-Null");
        sb.AppendLine("Write-Output 'OK'");

        var r = await PowerShellRunner.RunAsync(sb.ToString(), 30_000, ct).ConfigureAwait(false);
        if (!r.Ok)
            Log?.Invoke($"Не удалось создать правило брандмауэра «{name}»: {r.Combined.Trim()}");
        return r.Ok;
    }

    /// <summary>Удалить правило по имени.</summary>
    public async Task<bool> RemoveRuleAsync(string name, CancellationToken ct = default)
    {
        string script = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            Remove-NetFirewallRule -Name '{{EscapePs(name)}}' -ErrorAction SilentlyContinue
            Write-Output 'OK'
            """;
        var r = await PowerShellRunner.RunAsync(script, 30_000, ct).ConfigureAwait(false);
        return r.Ok;
    }

    /// <summary>Удалить все правила группы приложения.</summary>
    public async Task<bool> RemoveGroupAsync(CancellationToken ct = default)
    {
        const string script = """
            $ErrorActionPreference = 'SilentlyContinue'
            Get-NetFirewallRule -Group 'ExitBoost' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
            Write-Output 'OK'
            """;
        var r = await PowerShellRunner.RunAsync(script, 30_000, ct).ConfigureAwait(false);
        return r.Ok;
    }

    /// <summary>Список правил, созданных приложением.</summary>
    public async Task<IReadOnlyList<FirewallRuleInfo>> GetRulesAsync(CancellationToken ct = default)    {
        const string script = """
            $ErrorActionPreference = 'SilentlyContinue'
            Get-NetFirewallRule -Group 'ExitBoost' -ErrorAction SilentlyContinue | ForEach-Object {
                $app = $null
                try { $app = $_.Get-NetFirewallApplicationFilter | Select-Object -ExpandProperty Program } catch {}
                [PSCustomObject]@{
                    Name = $_.Name
                    DisplayName = $_.DisplayName
                    Action = $_.Action.ToString()
                    Direction = $_.Direction.ToString()
                    Program = $app
                }
            } | ConvertTo-Json -Compress
            """;
        var r = await PowerShellRunner.RunAsync(script, 30_000, ct).ConfigureAwait(false);
        return ParseJsonArray<FirewallRuleInfo>(r.Output);
    }

    // ------------------------------------------------------------- утилиты

    /// <summary>
    /// Службы туннелей, оставшиеся от предыдущих запусков.
    /// Используется для очистки состояния при старте приложения.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetStaleTunnelServicesAsync(CancellationToken ct = default)
    {
        const string script = """
            $ErrorActionPreference = 'SilentlyContinue'
            Get-Service | Where-Object { $_.Name -like 'WireGuardTunnel$ExitBoost_*' } | ForEach-Object { $_.Name }
            """;
        var r = await PowerShellRunner.RunAsync(script, 30_000, ct).ConfigureAwait(false);
        return r.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.StartsWith("WireGuardTunnel$", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// Удалить адаптеры, оставшиеся от туннелей приложения
    /// (имена ExitBoost_* и тестовый warp-test). Чужие туннели не трогаются.
    /// </summary>
    public async Task<IReadOnlyList<string>> RemoveTunnelAdaptersAsync(CancellationToken ct = default)
    {
        const string script = """
            $ErrorActionPreference = 'SilentlyContinue'
            Get-NetAdapter | Where-Object {
                $_.Name -like 'ExitBoost_*' -or $_.Name -eq 'warp-test'
            } | ForEach-Object { $_.Name; Remove-NetAdapter -Name $_.Name -Confirm:$false }
            """;
        var r = await PowerShellRunner.RunAsync(script, 60_000, ct).ConfigureAwait(false);
        return r.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    /// <summary>Сброс кэша DNS.</summary>
    public async Task<bool> FlushDnsAsync(CancellationToken ct = default)
    {
        const string script = "ipconfig /flushdns | Out-Null; 'ok'";
        var r = await PowerShellRunner.RunAsync(script, 30_000, ct).ConfigureAwait(false);
        return r.Output.Contains("ok");
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static IReadOnlyList<T> ParseJsonArray<T>(string json)
    {
        var text = json.Trim();
        if (text.Length == 0) return Array.Empty<T>();
        try
        {
            if (text.StartsWith('['))
                return JsonSerializer.Deserialize<List<T>>(text, JsonOpts) ?? new List<T>();
            var single = JsonSerializer.Deserialize<T>(text, JsonOpts);
            return single is null ? Array.Empty<T>() : new List<T> { single };
        }
        catch
        {
            return Array.Empty<T>();
        }
    }

    /// <summary>Экранирование строки для вставки в PowerShell single-quoted литерал.</summary>
    public static string EscapePs(string value) => value.Replace("'", "''");
}
