using Bunit;
using FluxorViewer.Server.Components;

namespace FluxorViewer.Server.Tests;

// UI-4: how the State tab's JSON tree renders and responds to toggles.
public class JsonTreeTests : BunitContext
{
    private IRenderedComponent<JsonTree> Tree(string json) => Render<JsonTree>(p => p.Add(t => t.Json, json));

    private static string[] Paths(IRenderedComponent<JsonTree> tree) =>
        [.. tree.FindAll("[data-test=node]").Select(n => n.GetAttribute("data-path")!)];

    [Fact]
    public void The_root_and_its_direct_children_are_open_by_default()
    {
        var tree = Tree("""{"a":{"b":{"c":1}},"n":2}""");

        Assert.Equal(["$.a", "$.a.b", "$.n"], Paths(tree));
        Assert.Equal("true", tree.Find("[data-path='$.a'] > [data-test=toggle]").GetAttribute("aria-expanded"));
        Assert.Equal("false", tree.Find("[data-path='$.a.b'] > [data-test=toggle]").GetAttribute("aria-expanded"));
    }

    [Fact]
    public void Toggle_buttons_open_and_close_a_node_rendering_children_only_while_open()
    {
        var tree = Tree("""{"a":{"b":{"c":1}}}""");

        tree.Find("[data-path='$.a.b'] > button[data-test=toggle]").Click();
        Assert.Equal(["$.a", "$.a.b", "$.a.b.c"], Paths(tree));

        tree.Find("[data-path='$.a'] > button[data-test=toggle]").Click();
        Assert.Equal(["$.a"], Paths(tree));
        Assert.Equal("{…} 1 key", tree.Find("[data-path='$.a'] > [data-test=summary]").TextContent.Trim());
    }

    [Fact]
    public void Collapsed_objects_and_arrays_show_how_much_they_hold()
    {
        var tree = Tree("""{"a":{"one":{"x":1},"many":{"x":1,"y":2},"list":[1,2,3],"single":[0],"none":[]}}""");

        string Summary(string path) => tree.Find($"[data-path='{path}'] > [data-test=summary]").TextContent.Trim();
        Assert.Equal("{…} 1 key", Summary("$.a.one"));
        Assert.Equal("{…} 2 keys", Summary("$.a.many"));
        Assert.Equal("[…] 3 items", Summary("$.a.list"));
        Assert.Equal("[…] 1 item", Summary("$.a.single"));
        Assert.Equal("[…] 0 items", Summary("$.a.none"));
    }

    [Fact]
    public void Values_show_as_received_tagged_with_their_kind_in_property_order()
    {
        var tree = Tree("""{"z":"hi \"there\"","big":9007199254740993,"f":0.30000000000000004,"e":1E+2,"t":true,"n":null}""");

        Assert.Equal(["z", "big", "f", "e", "t", "n"], tree.FindAll("[data-test=key]").Select(k => k.TextContent));
        Assert.Equal(
            [("string", "\"hi \\\"there\\\"\""), ("number", "9007199254740993"), ("number", "0.30000000000000004"),
             ("number", "1E+2"), ("boolean", "true"), ("null", "null")],
            tree.FindAll("[data-test=value]").Select(v => (v.GetAttribute("data-kind"), v.TextContent)));
    }

    private static string Numbers(int count) => $"[{string.Join(",", Enumerable.Range(0, count))}]";

    [Fact]
    public void Arrays_over_100_items_are_split_into_collapsed_ranges_of_100()
    {
        var tree = Tree($$"""{"list":{{Numbers(250)}}}""");

        Assert.Equal(["$.list", "$.list[0…99]", "$.list[100…199]", "$.list[200…249]"], Paths(tree));
        Assert.Equal(["list", "[0…99]", "[100…199]", "[200…249]"], tree.FindAll("[data-test=key]").Select(k => k.TextContent));
        Assert.All(tree.FindAll("[data-path^='$.list['] > [data-test=toggle]"), t => Assert.Equal("false", t.GetAttribute("aria-expanded")));

        tree.Find("[data-path='$.list[200…249]'] > [data-test=toggle]").Click();
        var range = tree.FindAll("[data-path^='$.list[2']").Select(n => n.GetAttribute("data-path")).ToArray();
        Assert.Equal(51, range.Length);
        Assert.Equal("$.list[200]", range[1]);
        Assert.Equal("249", tree.Find("[data-path='$.list[249]'] > [data-test=value]").TextContent);
    }

    [Fact]
    public void An_array_of_100_items_is_not_split()
    {
        var tree = Tree($$"""{"list":{{Numbers(100)}}}""");

        Assert.Equal(101, Paths(tree).Length);
    }

    [Fact]
    public void A_root_array_lists_its_items_and_a_root_value_shows_on_its_own()
    {
        Assert.Equal(["$[0]", "$[1]"], Paths(Tree("[1,2]")));

        var value = Tree("42");
        Assert.Empty(Paths(value));
        Assert.Equal("42", value.Find("[data-test=value]").TextContent);
    }

    [Fact]
    public void Property_names_that_are_not_plain_identifiers_get_unambiguous_paths()
    {
        var tree = Tree("""{"a.b":1,"a":{"b":2},"x y":3,"q\"":4}""");

        Assert.Equal(["""$["a.b"]""", "$.a", "$.a.b", """$["x y"]""", """$["q\""]"""], Paths(tree));
    }

    [Fact]
    public void Array_items_are_keyed_by_index()
    {
        var tree = Tree("""{"list":[10,{"x":1}]}""");

        Assert.Equal(["$.list", "$.list[0]", "$.list[1]"], Paths(tree));
        Assert.Equal(["list", "0", "1"], tree.FindAll("[data-test=key]").Select(k => k.TextContent));
    }
}
