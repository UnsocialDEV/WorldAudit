using System.Net;
using System.Net.Sockets;
using WorldAudit.Application;
using Vintagestory.API.Server;

namespace WorldAudit.Mod.Telemetry;

internal sealed class WorldAuditTelemetryService : IDisposable
{
    private readonly ICoreServerAPI _api;
    private readonly Func<WorldAuditRuntime?> _runtimeAccessor;
    private readonly Func<WorldAuditModConfig?> _configAccessor;
    private readonly Func<string> _worldIdAccessor;
    private readonly WorldAuditTelemetryPayloadFactory _payloadFactory;
    private DiscordWebhookClient? _client;
    private string? _currentWebhookUrl;
    private TimeSpan _currentTimeout;
    private bool _globalHandlersRegistered;

    public WorldAuditTelemetryService(
        ICoreServerAPI api,
        Func<WorldAuditRuntime?> runtimeAccessor,
        Func<WorldAuditModConfig?> configAccessor,
        Func<string> worldIdAccessor)
    {
        _api = api;
        _runtimeAccessor = runtimeAccessor;
        _configAccessor = configAccessor;
        _worldIdAccessor = worldIdAccessor;
        _payloadFactory = new WorldAuditTelemetryPayloadFactory();
        RefreshConfiguration();
    }

    public void RegisterGlobalHandlers()
    {
        if (_client is null || _globalHandlersRegistered)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        _globalHandlersRegistered = true;
    }

    public void ReportStartup()
    {
        RefreshConfiguration();
        var config = _configAccessor();
        if (_client is null || config is null)
        {
            return;
        }

        var message = _payloadFactory.CreateStartupMessage(
            GetServerEndpoint(),
            config,
            _runtimeAccessor(),
            _worldIdAccessor(),
            GetWorldName(),
            GetOnlinePlayerNames(config));

        Send(message, "startup telemetry");
    }

    public void ReportOperationFailure(string operation, Exception exception, string? detail = null)
    {
        RefreshConfiguration();
        var config = _configAccessor();
        if (_client is null || config is null)
        {
            return;
        }

        var message = _payloadFactory.CreateErrorMessage(
            GetServerEndpoint(),
            operation,
            exception,
            _runtimeAccessor(),
            _worldIdAccessor(),
            GetWorldName(),
            GetOnlinePlayerNames(config),
            detail);

        Send(message, "error telemetry");
    }

    public void ReportMaintenanceFailure(MaintenanceRunStatus status)
    {
        RefreshConfiguration();
        var config = _configAccessor();
        if (_client is null || config is null || status.Succeeded)
        {
            return;
        }

        var message = _payloadFactory.CreateMaintenanceFailureMessage(
            GetServerEndpoint(),
            status,
            _runtimeAccessor(),
            _worldIdAccessor(),
            GetWorldName(),
            GetOnlinePlayerNames(config));

        Send(message, "maintenance telemetry");
    }

    public void Dispose()
    {
        UnregisterGlobalHandlers();
        _client?.Dispose();
        _client = null;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
        {
            ReportOperationFailure("unhandled exception", exception, $"IsTerminating={args.IsTerminating}");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        ReportOperationFailure("unobserved task exception", args.Exception);
    }

    private void Send(DiscordWebhookMessage message, string operation)
    {
        try
        {
            using var response = _client?.SendAsync(message).GetAwaiter().GetResult();
            if (response is not null && !response.IsSuccessStatusCode)
            {
                _api.Logger.Warning(
                    "[WorldAudit] Discord telemetry for {0} returned HTTP {1}.",
                    operation,
                    (int)response.StatusCode);
            }
        }
        catch (Exception exception)
        {
            _api.Logger.Warning("[WorldAudit] Failed to send Discord telemetry for {0}: {1}", operation, exception.Message);
        }
    }

    private void RefreshConfiguration()
    {
        var config = _configAccessor();
        if (config is not { EnableDiscordTelemetry: true } || string.IsNullOrWhiteSpace(config.DiscordWebhookUrl))
        {
            UnregisterGlobalHandlers();
            _client?.Dispose();
            _client = null;
            _currentWebhookUrl = null;
            _currentTimeout = TimeSpan.Zero;
            return;
        }

        var timeout = TimeSpan.FromSeconds(config.DiscordTelemetryTimeoutSeconds);
        if (_client is not null
            && string.Equals(_currentWebhookUrl, config.DiscordWebhookUrl, StringComparison.Ordinal)
            && _currentTimeout == timeout)
        {
            RegisterGlobalHandlers();
            return;
        }

        _client?.Dispose();
        _client = new DiscordWebhookClient(
            config.DiscordWebhookUrl,
            timeout);
        _currentWebhookUrl = config.DiscordWebhookUrl;
        _currentTimeout = timeout;
        RegisterGlobalHandlers();
    }

    private WorldAuditOnlinePlayerSnapshot GetOnlinePlayerNames(WorldAuditModConfig config)
    {
        var names = _api.World.AllOnlinePlayers
            .Select(player => player?.PlayerName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return config.IncludeOnlinePlayerNamesInTelemetry
            ? new WorldAuditOnlinePlayerSnapshot(names.Length, names)
            : new WorldAuditOnlinePlayerSnapshot(names.Length, Array.Empty<string>());
    }

    private string GetWorldName()
    {
        return _api.World.WorldName ?? "main";
    }

    private WorldAuditServerEndpointSnapshot GetServerEndpoint()
    {
        var ipAddress = ResolveServerIpAddress(_api.Server.ServerIp);
        var port = _api.Server.Config.Port;
        return new WorldAuditServerEndpointSnapshot(ipAddress, port);
    }

    internal static string ResolveServerIpAddress(string? configuredAddress)
    {
        if (!IsFallbackAddressRequired(configuredAddress))
        {
            return configuredAddress!;
        }

        foreach (var address in GetCandidateHostAddresses())
        {
            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                continue;
            }

            if (IPAddress.IsLoopback(address))
            {
                continue;
            }

            var text = address.ToString();
            if (!IsFallbackAddressRequired(text))
            {
                return text;
            }
        }

        return "unknown";
    }

    internal static bool IsFallbackAddressRequired(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return true;
        }

        return string.Equals(address, "0.0.0.0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(address, "::", StringComparison.OrdinalIgnoreCase)
            || string.Equals(address, "::0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(address, "::1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(address, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(address, "unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<IPAddress> GetCandidateHostAddresses()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName());
        }
        catch
        {
            return Array.Empty<IPAddress>();
        }
    }

    private void UnregisterGlobalHandlers()
    {
        if (!_globalHandlersRegistered)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        _globalHandlersRegistered = false;
    }
}
