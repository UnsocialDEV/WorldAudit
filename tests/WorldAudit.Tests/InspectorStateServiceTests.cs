using WorldAudit.Application.Services;
using WorldAudit.Domain;

namespace WorldAudit.Tests;

public sealed class InspectorStateServiceTests
{
    [Fact]
    public void Toggle_EnablesAndThenDisablesInspectorMode()
    {
        var service = new InspectorStateService();

        var enabled = service.Toggle("player-1");
        var disabled = service.Toggle("player-1");

        Assert.True(enabled);
        Assert.False(disabled);
        Assert.False(service.IsEnabled("player-1"));
    }

    [Fact]
    public void Remove_ClearsEnabledState()
    {
        var service = new InspectorStateService();
        service.Toggle("player-1");

        service.Remove("player-1");

        Assert.False(service.IsEnabled("player-1"));
    }

    [Fact]
    public void GetEnabledPlayerUids_ReturnsEnabledPlayersOnly()
    {
        var service = new InspectorStateService();
        service.Toggle("player-1");
        service.Toggle("player-2");
        service.Remove("player-1");

        var enabledPlayers = service.GetEnabledPlayerUids();

        Assert.Single(enabledPlayers);
        Assert.Contains("player-2", enabledPlayers);
    }

    [Fact]
    public void AdvancePage_CyclesForSameLocation_AndResetsForDifferentLocation()
    {
        var service = new InspectorStateService();
        var position = new BlockPosition(4, 65, 7);
        var otherPosition = new BlockPosition(8, 65, 7);

        service.Toggle("player-1");

        var firstPage = service.AdvancePage("player-1", "main", position, totalEventCount: 7, pageSize: 3);
        var secondPage = service.AdvancePage("player-1", "main", position, totalEventCount: 7, pageSize: 3);
        var thirdPage = service.AdvancePage("player-1", "main", position, totalEventCount: 7, pageSize: 3);
        var wrappedPage = service.AdvancePage("player-1", "main", position, totalEventCount: 7, pageSize: 3);
        var resetPage = service.AdvancePage("player-1", "main", otherPosition, totalEventCount: 7, pageSize: 3);

        Assert.Equal(1, firstPage.PageNumber);
        Assert.Equal(2, secondPage.PageNumber);
        Assert.Equal(3, thirdPage.PageNumber);
        Assert.Equal(1, wrappedPage.PageNumber);
        Assert.Equal(1, resetPage.PageNumber);
    }
}
