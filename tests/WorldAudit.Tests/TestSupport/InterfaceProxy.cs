using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace WorldAudit.Tests.TestSupport;

internal class InterfaceProxy<T> : DispatchProxy
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, System.Func<object?[]?, object?>> _handlers = new(StringComparer.Ordinal);

    internal object? Read(string name) => _values.GetValueOrDefault(name);
    internal void Call(string name, params object?[] args)
    {
        if (_handlers.TryGetValue(name, out var handler)) handler(args);
    }

    public void SetValue(string memberName, object? value)
    {
        _values[memberName] = value;
    }

    public void SetHandler(string methodName, System.Func<object?[]?, object?> handler)
    {
        _handlers[methodName] = handler;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
        {
            throw new ArgumentNullException(nameof(targetMethod));
        }

        if (_handlers.TryGetValue(targetMethod.Name, out var handler))
        {
            return handler(args);
        }

        if (targetMethod.Name.StartsWith("get_", StringComparison.Ordinal))
        {
            var memberName = targetMethod.Name[4..];
            if (_values.TryGetValue(memberName, out var value))
            {
                return value;
            }
        }

        if (targetMethod.Name.StartsWith("set_", StringComparison.Ordinal))
        {
            _values[targetMethod.Name[4..]] = args?[0];
            return null;
        }

        if (targetMethod.Name == nameof(object.ToString))
        {
            return typeof(T).Name;
        }

        return targetMethod.ReturnType == typeof(void)
            ? null
            : targetMethod.ReturnType.IsValueType
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
    }
}

internal static class ProxyFactory
{
    public static T Create<T>(Action<InterfaceProxy<T>> configure) where T : class
    {
        // 1.22.7 has an internal abstract IPlayer member that DispatchProxy cannot implement.
        if (typeof(T) == typeof(IPlayer) || typeof(T) == typeof(IServerPlayer))
        {
            var configuration = new InterfaceProxy<T>();
            configure(configuration);
            var player = (TestPlayer)RuntimeHelpers.GetUninitializedObject(typeof(TestPlayer));
            player.Name = (string?)configuration.Read(nameof(IPlayer.PlayerName)) ?? "test";
            player.Uid = (string?)configuration.Read(nameof(IPlayer.PlayerUID)) ?? "test-uid";
            player.MessageHandler = args => configuration.Call(nameof(IServerPlayer.SendMessage), args);
            return (T)(object)player;
        }
        var proxy = DispatchProxy.Create<T, InterfaceProxy<T>>();
        configure((InterfaceProxy<T>)(object)proxy);
        return proxy;
    }
}

internal sealed class TestPlayer : ServerPlayer, IServerPlayer
{
    private TestPlayer() : base(null!, null!) { }
    internal string Name = "";
    internal string Uid = "";
    internal Action<object?[]>? MessageHandler;
    string IPlayer.PlayerName => Name;
    string IPlayer.PlayerUID => Uid;
    void IServerPlayer.SendMessage(int groupId, string message, EnumChatType chatType, string data)
        => MessageHandler?.Invoke([groupId, message, chatType, data]);
}
