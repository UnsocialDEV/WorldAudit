using System.Reflection;
using Vintagestory.API.Common;
using WorldAudit.Mod;

namespace WorldAudit.Tests;

public sealed class WorldAuditModConfigTests
{
    [Fact]
    public void ToRuntimeOptions_ResolvesRelativeDatabasePath()
    {
        var config = new WorldAuditModConfig
        {
            DatabasePath = "data\\worldaudit.db",
            WriterBatchSize = 64,
            WriterMaxFlushDelayMilliseconds = 150,
            RetentionDays = 14,
            CheckpointIntervalMinutes = 45,
            CheckpointWalSizeMegabytes = 32,
            OptimizeIntervalHours = 12,
            SlowQueryThresholdMilliseconds = 10,
            SlowFlushThresholdMilliseconds = 20,
            EnableWorldgenCauseProvider = false
        };

        var api = DispatchProxy.Create<ICoreAPICommon, CoreApiCommonProxy>();
        ((CoreApiCommonProxy)(object)api).GetOrCreateDataPathResult = Path.Combine(Path.GetTempPath(), "worldaudit-tests");

        var options = config.ToRuntimeOptions(api);

        Assert.True(Path.IsPathRooted(options.DatabasePath));
        Assert.StartsWith(((CoreApiCommonProxy)(object)api).GetOrCreateDataPathResult, options.DatabasePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, options.WriterBatchSize);
        Assert.Equal(TimeSpan.FromMilliseconds(150), options.WriterMaxFlushDelay);
        Assert.Equal(14, options.RetentionDays);
        Assert.Equal(45, options.CheckpointIntervalMinutes);
        Assert.Equal(32, options.CheckpointWalSizeMegabytes);
        Assert.Equal(12, options.OptimizeIntervalHours);
        Assert.Equal(10, options.SlowQueryThresholdMilliseconds);
        Assert.Equal(20, options.SlowFlushThresholdMilliseconds);
        Assert.False(options.EnableWorldgenCauseProvider);
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenDiscordTelemetryEnabledWithoutWebhook()
    {
        var config = new WorldAuditModConfig
        {
            EnableDiscordTelemetry = true,
            DiscordWebhookUrl = string.Empty
        };

        Assert.False(config.IsValid);
    }

    [Fact]
    public void IsValid_ReturnsTrue_WhenDiscordTelemetryDisabledWithoutWebhook()
    {
        var config = new WorldAuditModConfig
        {
            EnableDiscordTelemetry = false,
            DiscordWebhookUrl = string.Empty,
            DiscordChannelId = string.Empty
        };

        Assert.True(config.IsValid);
    }

    private class CoreApiCommonProxy : DispatchProxy
    {
        public string GetOrCreateDataPathResult { get; set; } = string.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
            {
                throw new ArgumentNullException(nameof(targetMethod));
            }

            if (targetMethod.Name == nameof(ICoreAPICommon.GetOrCreateDataPath))
            {
                return GetOrCreateDataPathResult;
            }

            return targetMethod.ReturnType.IsValueType
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }
}
