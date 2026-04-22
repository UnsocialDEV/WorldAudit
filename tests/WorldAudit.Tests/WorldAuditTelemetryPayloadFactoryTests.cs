using WorldAudit.Application;
using System.Net;
using WorldAudit.Mod;
using WorldAudit.Mod.Telemetry;

namespace WorldAudit.Tests;

public sealed class WorldAuditTelemetryPayloadFactoryTests
{
    [Fact]
    public void CreateStartupMessage_IncludesRuntimeAndEndpointDetails()
    {
        var factory = new WorldAuditTelemetryPayloadFactory();
        var config = new WorldAuditModConfig
        {
            IncludeOnlinePlayerNamesInTelemetry = true
        };
        var endpoint = new WorldAuditServerEndpointSnapshot("203.0.113.10", 42420);

        var message = factory.CreateStartupMessage(
            endpoint,
            config,
            runtime: null,
            worldId: "main",
            worldName: "Test World",
            onlinePlayers: new WorldAuditOnlinePlayerSnapshot(2, ["Alice", "Bob"]));

        Assert.Equal("WorldAudit Tracker", message.Username);
        Assert.Contains("main", message.Content, StringComparison.Ordinal);
        Assert.Contains(message.Embeds, embed => embed.Fields.Any(field => field.Name == "Server Endpoint" && field.Value == endpoint.DisplayValue));
        Assert.Contains(message.Embeds, embed => embed.Fields.Any(field => field.Name == "Online Players" && field.Value.Contains("Alice", StringComparison.Ordinal)));
    }

    [Fact]
    public void CreateErrorMessage_UsesExceptionInformation()
    {
        var factory = new WorldAuditTelemetryPayloadFactory();
        var exception = new InvalidOperationException("boom", new ArgumentException("bad arg"));
        var endpoint = new WorldAuditServerEndpointSnapshot("203.0.113.10", 42420);

        var message = factory.CreateErrorMessage(
            endpoint,
            "reload",
            exception,
            runtime: null,
            worldId: "main",
            worldName: "Test World",
            onlinePlayers: new WorldAuditOnlinePlayerSnapshot(0, Array.Empty<string>()),
            detail: "during test");

        var embed = Assert.Single(message.Embeds);
        Assert.Contains("Type: System.InvalidOperationException", embed.Description, StringComparison.Ordinal);
        Assert.Contains("during test", embed.Description, StringComparison.Ordinal);
        Assert.Contains(embed.Fields, field => field.Name == "Operation" && field.Value == "reload");
        Assert.Contains(embed.Fields, field => field.Name == "Server Endpoint" && field.Value == endpoint.DisplayValue);
    }

    [Fact]
    public void CreateMaintenanceFailureMessage_ContainsFailureSummary()
    {
        var factory = new WorldAuditTelemetryPayloadFactory();
        var endpoint = new WorldAuditServerEndpointSnapshot("203.0.113.10", 42420);
        var status = new MaintenanceRunStatus(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow,
            false,
            14,
            DateTimeOffset.UtcNow.AddDays(-14),
            "Maintenance failed.",
            "Checkpoint crashed.");

        var message = factory.CreateMaintenanceFailureMessage(
            endpoint,
            status,
            runtime: null,
            worldId: "main",
            worldName: "Test World",
            onlinePlayers: new WorldAuditOnlinePlayerSnapshot(0, Array.Empty<string>()));

        var embed = Assert.Single(message.Embeds);
        Assert.Contains("Checkpoint crashed.", embed.Description, StringComparison.Ordinal);
        Assert.Contains(embed.Fields, field => field.Name == "Operation" && field.Value == "maintenance");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("localhost")]
    [InlineData("unknown")]
    public void IsFallbackAddressRequired_ReturnsTrue_ForBindOrMissingAddresses(string? address)
    {
        Assert.True(WorldAuditTelemetryService.IsFallbackAddressRequired(address));
    }

    [Theory]
    [InlineData("203.0.113.10")]
    [InlineData("192.168.1.15")]
    public void IsFallbackAddressRequired_ReturnsFalse_ForRealAddresses(string address)
    {
        Assert.False(WorldAuditTelemetryService.IsFallbackAddressRequired(address));
    }

    [Fact]
    public void ResolveServerIpAddress_ReturnsConfiguredAddress_WhenUsable()
    {
        Assert.Equal("203.0.113.10", WorldAuditTelemetryService.ResolveServerIpAddress("203.0.113.10"));
    }
}
