namespace FluxorViewer.Server.Components;

/// <summary>
/// What the developer chose in the right pane of one Viewer tab: which tab shows, Show unchanged, whether the Payload is
/// expanded, and the JSON trees' expand/collapse choices. Each is kept whatever the Action, Feature or Session, so the
/// page owns it rather than the panes, which unmount when the Session changes.
/// </summary>
public sealed class RightPaneState
{
    /// <summary>The tab showing: State until the developer picks Diff.</summary>
    public RightTab Tab { get; set; } = RightTab.State;

    /// <summary>Whether the Diff tab shows unchanged values too.</summary>
    public bool ShowUnchanged { get; set; }

    /// <summary>Whether the Payload shows as a tree rather than one line.</summary>
    public bool PayloadExpanded { get; set; }

    /// <summary>The State tab's tree choices, per Feature name.</summary>
    public TreeChoices StateChoices { get; } = new();

    /// <summary>The Payload tree's choices, per full Action type.</summary>
    public TreeChoices PayloadChoices { get; } = new();
}

/// <summary>The right pane's tabs, in the order they show.</summary>
public enum RightTab
{
    /// <summary>The selected Feature's state after the selected Action.</summary>
    State,

    /// <summary>What the selected Action changed in the selected Feature.</summary>
    Diff,
}
