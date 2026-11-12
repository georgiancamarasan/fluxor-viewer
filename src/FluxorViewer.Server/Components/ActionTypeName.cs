using System.Text;

namespace FluxorViewer.Server.Components;

/// <summary>Short names for Action types, from the readable C# names the middleware sends.</summary>
public static class ActionTypeName
{
    /// <summary>
    /// Drops namespaces and enclosing types, in generic arguments too: <c>MyApp.Items.LoadedAction&lt;MyApp.Item&gt;</c>
    /// becomes <c>LoadedAction&lt;Item&gt;</c>. A nested type's enclosing type can't be told apart from a namespace,
    /// so it's dropped as well.
    /// </summary>
    public static string Short(string fullName)
    {
        var last = LastSegment(fullName);
        var open = last.IndexOf('<');
        if (open < 0)
            return last.ToString();

        var close = last.LastIndexOf('>');
        var result = new StringBuilder().Append(last[..open]).Append('<');
        var first = true;
        foreach (var argument in SplitTopLevel(last[(open + 1)..close], ','))
        {
            if (!first)
                result.Append(", ");
            result.Append(Short(argument.Trim().ToString()));
            first = false;
        }
        return result.Append('>').Append(last[(close + 1)..]).ToString();
    }

    /// <summary>The part after the last '.' outside generic brackets.</summary>
    private static ReadOnlySpan<char> LastSegment(ReadOnlySpan<char> name)
    {
        var depth = 0;
        for (var i = name.Length - 1; i >= 0; i--)
        {
            switch (name[i])
            {
                case '>': depth++; break;
                case '<': depth--; break;
                case '.' when depth == 0: return name[(i + 1)..];
            }
        }
        return name;
    }

    private static List<string> SplitTopLevel(ReadOnlySpan<char> text, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<' or '[': depth++; break;
                case '>' or ']': depth--; break;
                default:
                    if (text[i] == separator && depth == 0)
                    {
                        parts.Add(text[start..i].ToString());
                        start = i + 1;
                    }
                    break;
            }
        }
        parts.Add(text[start..].ToString());
        return parts;
    }
}
