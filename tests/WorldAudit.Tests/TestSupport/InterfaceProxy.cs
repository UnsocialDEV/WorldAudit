using System.Reflection;

namespace WorldAudit.Tests.TestSupport;

internal class InterfaceProxy<T> : DispatchProxy
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<object?[]?, object?>> _handlers = new(StringComparer.Ordinal);

    public void SetValue(string memberName, object? value)
    {
        _values[memberName] = value;
    }

    public void SetHandler(string methodName, Func<object?[]?, object?> handler)
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
        var proxy = DispatchProxy.Create<T, InterfaceProxy<T>>();
        configure((InterfaceProxy<T>)(object)proxy);
        return proxy;
    }
}
