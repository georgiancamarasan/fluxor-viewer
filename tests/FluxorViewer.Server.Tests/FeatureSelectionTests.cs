using FluxorViewer.Server.Components;

namespace FluxorViewer.Server.Tests;

// UI-2: which Feature the picker lists and selects.
public class FeatureSelectionTests
{
    private readonly FeatureSelection selection = new();

    [Fact]
    public void Features_are_listed_alphabetically_ignoring_case()
    {
        selection.Update(["ZooState", "authState", "Counter"]);

        Assert.Equal(["authState", "Counter", "ZooState"], selection.Features);
    }

    [Fact]
    public void Without_a_pick_the_first_Feature_is_selected()
    {
        selection.Update(["Counter", "Auth"]);

        Assert.Equal("Auth", selection.Selected);
    }

    [Fact]
    public void Nothing_is_selected_without_Features()
    {
        selection.Update(["Counter"]);
        selection.Update([]);

        Assert.Empty(selection.Features);
        Assert.Null(selection.Selected);
    }

    [Fact]
    public void The_pick_stays_selected_in_another_Session_that_has_it()
    {
        selection.Update(["Auth", "Counter"]);
        selection.Select("Counter");

        selection.Update(["Auth", "Counter", "Weather"]);

        Assert.Equal("Counter", selection.Selected);
    }

    [Fact]
    public void A_Session_without_the_pick_falls_back_to_its_first_Feature()
    {
        selection.Update(["Auth", "Counter"]);
        selection.Select("Counter");

        selection.Update(["Weather", "Basket"]);

        Assert.Equal("Basket", selection.Selected);
    }

    [Fact]
    public void A_fallback_does_not_overwrite_the_pick()
    {
        selection.Update(["Auth", "Counter"]);
        selection.Select("Counter");
        selection.Update(["Basket"]);

        selection.Update(["Auth", "Counter"]);

        Assert.Equal("Counter", selection.Selected);
    }

    [Fact]
    public void The_pick_is_matched_by_exact_name()
    {
        selection.Update(["Counter"]);
        selection.Select("Counter");

        selection.Update(["Auth", "counter"]);

        Assert.Equal("Auth", selection.Selected);
    }
}
