using System.Collections.Frozen;
using System.Collections.Immutable;
using FluxorViewer.Protocol;
using FluxorViewer.Server.Components;
using FluxorViewer.Server.Sessions;

namespace FluxorViewer.Server.Tests;

// UI-5: what the Diff tab shows for a Feature at an Action, before anything reaches the browser.
public class FeatureDiffTests
{
    private static ImmutableDictionary<string, FeatureState> Map(params (string Name, string Json)[] features) =>
        ImmutableDictionary.CreateRange(features.Select(f => KeyValuePair.Create(f.Name, new FeatureState(f.Name, $"App.{f.Name}State", f.Json))));

    private static ActionEntry Action(
        ImmutableDictionary<string, FeatureState> before, ImmutableDictionary<string, FeatureState> after, params string[] changed) =>
        new(1, 1, DateTimeOffset.UtcNow, "App.Act", "{}", changed.ToFrozenSet(), before, after);

    [Fact]
    public void Without_an_Action_there_is_nothing_to_diff() =>
        Assert.IsType<FeatureDiff.NoAction>(FeatureDiff.For(null, "Counter"));

    [Fact]
    public void A_Feature_absent_after_the_Action_did_not_exist_at_that_point() =>
        Assert.IsType<FeatureDiff.Missing>(FeatureDiff.For(Action(Map(), Map(("Auth", "{}"))), "Counter"));

    [Fact]
    public void A_Feature_the_Action_did_not_replace_is_unchanged()
    {
        var map = Map(("Counter", """{"n":1}"""));

        Assert.IsType<FeatureDiff.NoChange>(FeatureDiff.For(Action(map, map), "Counter"));
    }

    [Fact]
    public void A_Feature_replaced_with_the_same_JSON_is_identical() =>
        Assert.IsType<FeatureDiff.Identical>(FeatureDiff.For(
            Action(Map(("Counter", """{"n":1}""")), Map(("Counter", """{"n":1}""")), "Counter"), "Counter"));

    [Fact]
    public void Different_JSON_goes_to_the_browser_with_both_sides()
    {
        var diff = FeatureDiff.For(Action(Map(("Counter", """{"n":1}""")), Map(("Counter", """{"n":2}""")), "Counter"), "Counter");

        Assert.Equal(new FeatureDiff.Changes("""{"n":1}""", """{"n":2}"""), diff);
    }

    [Fact]
    public void A_Feature_that_did_not_exist_before_is_diffed_against_nothing()
    {
        var diff = FeatureDiff.For(Action(Map(), Map(("Counter", """{"n":1}""")), "Counter"), "Counter");

        Assert.Equal(new FeatureDiff.Changes(null, """{"n":1}"""), diff);
    }

    [Theory]
    [InlineData("""{"$error":"Cycle"}""", """{"n":1}""", DiffSide.Before)]
    [InlineData("""{"n":1}""", """{"$error":"Cycle"}""", DiffSide.After)]
    [InlineData("""{"$error":"Cycle"}""", """{"$error":"Cycle"}""", DiffSide.After)]
    public void A_side_that_failed_to_serialise_is_an_error_instead_of_a_diff(string before, string after, DiffSide side) =>
        Assert.Equal(new FeatureDiff.Error(side, "Cycle"), FeatureDiff.For(Action(Map(("Counter", before)), Map(("Counter", after)), "Counter"), "Counter"));

    [Fact]
    public void Other_error_properties_are_ordinary_data() =>
        Assert.IsType<FeatureDiff.Changes>(FeatureDiff.For(
            Action(Map(("Counter", """{"$error":"x","n":1}""")), Map(("Counter", """{"$error":"x","n":2}""")), "Counter"), "Counter"));
}
