namespace FluxorViewer.Middleware;

/// <summary>Readable C#-style type names, e.g. <c>MyApp.Store.Counter.LoadedAction&lt;MyApp.Item&gt;</c>.</summary>
internal static class TypeNames
{
    public static string Readable(Type type)
    {
        if (type.IsGenericParameter)
            return type.Name;
        if (type.IsArray)
            return $"{Readable(type.GetElementType()!)}[{new string(',', type.GetArrayRank() - 1)}]";

        // A nested type's generic arguments include those of its enclosing types, outermost first.
        var chain = new Stack<Type>();
        for (var t = type; t is not null; t = t.IsNested ? t.DeclaringType : null)
            chain.Push(t);

        var arguments = type.GetGenericArguments();
        var used = 0;
        var parts = new List<string>();
        if (type.Namespace is { } ns)
            parts.Add(ns);

        foreach (var level in chain)
        {
            var tick = level.Name.IndexOf('`');
            if (tick < 0)
            {
                parts.Add(level.Name);
                continue;
            }

            var count = int.Parse(level.Name.AsSpan(tick + 1));
            var own = arguments.Skip(used).Take(count).Select(Readable);
            parts.Add($"{level.Name[..tick]}<{string.Join(", ", own)}>");
            used += count;
        }

        return string.Join('.', parts);
    }
}
