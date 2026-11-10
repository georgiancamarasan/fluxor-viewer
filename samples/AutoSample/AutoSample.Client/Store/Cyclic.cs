using Fluxor;

namespace AutoSample.Client.Store;

/// <summary>
/// State whose object graph contains cycles (each child points back to its parent).
/// It is serialisable once cycles are ignored, so the Viewer should show it, not an error.
/// </summary>
[FeatureState]
public sealed record CyclicState(TreeNode Root)
{
    private CyclicState() : this(TreeNode.Build(0)) { }
}

public sealed class TreeNode
{
    public required string Name { get; init; }
    public TreeNode? Parent { get; init; }
    public List<TreeNode> Children { get; } = [];

    public static TreeNode Build(int childCount)
    {
        var root = new TreeNode { Name = "root" };
        for (var i = 1; i <= childCount; i++)
            root.Children.Add(new TreeNode { Name = $"child {i}", Parent = root });
        return root;
    }
}

public sealed record AddCyclicChildAction;

public static class CyclicReducers
{
    [ReducerMethod(typeof(AddCyclicChildAction))]
    public static CyclicState AddChild(CyclicState state) =>
        new(TreeNode.Build(state.Root.Children.Count + 1));
}
