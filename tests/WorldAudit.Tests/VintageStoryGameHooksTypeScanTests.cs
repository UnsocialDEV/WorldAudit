using System.Reflection;
using Vintagestory.API.Common;
using WorldAudit.Integration.VintageStory;
using WorldAudit.Tests.TestSupport;

namespace WorldAudit.Tests;

public sealed class VintageStoryGameHooksTypeScanTests
{
    [Fact]
    public void MissingOptionalDependencies_RetainsLoadableTypes_AndReportsLoaderErrors()
    {
        var errors = new List<string>();
        var logger = ProxyFactory.Create<ILogger>(proxy => proxy.SetHandler("Warning", args =>
        {
            errors.Add((string)args![0]!);
            return null;
        }));
        var assembly = new TestAssembly([], new ReflectionTypeLoadException(
            [typeof(Block), null!, typeof(BlockEntity), null!],
            [new FileNotFoundException("csogg is unavailable"), new FileNotFoundException("OpenTK.Graphics is unavailable")]));

        var types = VintageStoryGameHooks.GetLoadableTypes(assembly, logger);

        Assert.Equal([typeof(Block), typeof(BlockEntity)], types);
        var warning = Assert.Single(errors);
        Assert.Contains("scanning 2 loadable types; 2 types unavailable", warning);
        Assert.Contains("csogg", warning);
        Assert.Contains("OpenTK.Graphics", warning);
    }

    [Fact]
    public void CompleteAssembly_ReturnsAllTypes()
    {
        Type[] expected = [typeof(Block), typeof(BlockEntity)];
        Assert.Equal(expected, VintageStoryGameHooks.GetLoadableTypes(new TestAssembly(expected), ProxyFactory.Create<ILogger>(_ => { })));
    }

    [Fact]
    public void UnexpectedFailure_IsNotSuppressed()
    {
        var failure = new InvalidOperationException("Unexpected reflection failure");
        var result = Assert.Throws<InvalidOperationException>(() => VintageStoryGameHooks.GetLoadableTypes(
            new TestAssembly([], failure), ProxyFactory.Create<ILogger>(_ => { })));
        Assert.Same(failure, result);
    }

    private sealed class TestAssembly(Type[] types, Exception? failure = null) : Assembly
    {
        public override Type[] GetTypes() => failure is null ? types : throw failure;
        public override AssemblyName GetName() => new("DedicatedServerFixture");
    }
}
