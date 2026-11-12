namespace FluxorViewer.Middleware;

/// <summary>Readable C#-style type names, e.g. <c>MyApp.Store.Counter.LoadedAction&lt;MyApp.Item&gt;</c>.</summary>
internal static class TypeNames
{
    /// <summary>The full name of <paramref name="type"/> as C# would write it, generic arguments and arrays included.</summary>
    public static string Readable(Type type) => type switch
    {
        { IsGenericParameter: true } => type.Name,
        { IsArray: true } => $"{Readable(type.GetElementType()!)}[{new string(',', type.GetArrayRank() - 1)}]",
        _ => Qualified(type),
    };

    /// <summary>The namespace, then each enclosing type from the outermost in, each with its own generic arguments.</summary>
    private static string Qualified(Type type)
    {
        // A nested type's generic arguments include those of its enclosing types, outermost first.
        var arguments = new Queue<Type>(type.GetGenericArguments());
        List<string> parts = [.. EnclosingTypes(type).Select(level => WithOwnArguments(level, arguments))];
        if (type.Namespace is { } @namespace)
            parts.Insert(0, @namespace);
        return string.Join('.', parts);
    }

    /// <summary><paramref name="type"/> and the types it's nested in, outermost first.</summary>
    private static Stack<Type> EnclosingTypes(Type type)
    {
        var levels = new Stack<Type>();
        for (var level = type; level is not null; level = level.IsNested ? level.DeclaringType : null)
            levels.Push(level);
        return levels;
    }

    /// <summary><paramref name="level"/>'s name with as many generic arguments as it declares, taken from the front of <paramref name="arguments"/>.</summary>
    private static string WithOwnArguments(Type level, Queue<Type> arguments)
    {
        var tick = level.Name.IndexOf('`');
        if (tick < 0)
            return level.Name;
        var count = int.Parse(level.Name.AsSpan(tick + 1));
        List<string> own = [.. Enumerable.Range(0, count).Select(_ => Readable(arguments.Dequeue()))];
        return $"{level.Name[..tick]}<{string.Join(", ", own)}>";
    }
}
