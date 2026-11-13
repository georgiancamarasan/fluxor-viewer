using System.Collections.Immutable;
using FluxorViewer.Protocol;

namespace FluxorViewer.Server.Sessions;

/// <summary>One point in a Session's History, with the Feature states after it.</summary>
/// <param name="Id">
/// Identifies the entry within its Session, increasing in the order entries were appended and never reused. Unlike the
/// Sequence, it's unique: a Resync shares its Sequence with the Action before it, and after an older Snapshot the
/// History may hold Actions with the same Sequence twice.
/// </param>
/// <param name="Sequence">The Action's Sequence, or the Snapshot's <c>LastSequence</c> for a Resync.</param>
/// <remarks>
/// There are exactly two kinds, <see cref="ActionEntry"/> and <see cref="ResyncEntry"/>, and callers that treat them
/// differently (the Action list, the State and Diff tabs) pattern-match on them. That is deliberate: what a row says or
/// a tab shows is the UI's business, so it stays in the components rather than becoming virtual members here.
/// </remarks>
public abstract record HistoryEntry(long Id, long Sequence, ImmutableDictionary<string, FeatureState> After);

/// <summary>An Action in the History.</summary>
/// <param name="ChangedFeatures">Names of the Features whose state this Action replaced.</param>
/// <param name="Before">Feature states before this Action. Unchanged Features share their instances with <paramref name="After"/>.</param>
public sealed record ActionEntry(
    long Id,
    long Sequence,
    DateTimeOffset Timestamp,
    string ActionType,
    string Payload,
    IReadOnlySet<string> ChangedFeatures,
    ImmutableDictionary<string, FeatureState> Before,
    ImmutableDictionary<string, FeatureState> After) : HistoryEntry(Id, Sequence, After);

/// <summary>A Snapshot that didn't follow on from what the Viewer already had.</summary>
/// <param name="Sequence">The Snapshot's <c>LastSequence</c>: the Resync sits just after the last Action it covers.</param>
/// <param name="ReceivedAt">When the Viewer received the Snapshot.</param>
/// <param name="Lost">How many Actions the Viewer missed before it; null when the Snapshot was older than what the Viewer had.</param>
/// <param name="FirstLost">
/// The Sequence of the first lost Action, the rest running up to <paramref name="Sequence"/>; null when the Viewer can't
/// tell which were lost.
/// </param>
/// <param name="After">The Snapshot's Feature states.</param>
public sealed record ResyncEntry(
    long Id,
    long Sequence,
    DateTimeOffset ReceivedAt,
    long? Lost,
    long? FirstLost,
    ImmutableDictionary<string, FeatureState> After) : HistoryEntry(Id, Sequence, After);

/// <summary>Feature states keyed by Feature name. Maps are immutable and share unchanged states.</summary>
internal static class FeatureMap
{
    public static readonly ImmutableDictionary<string, FeatureState> Empty = ImmutableDictionary<string, FeatureState>.Empty;

    public static ImmutableDictionary<string, FeatureState> From(IEnumerable<FeatureState> features) =>
        Empty.SetItems(features.Select(f => KeyValuePair.Create(f.Name, f)));
}
