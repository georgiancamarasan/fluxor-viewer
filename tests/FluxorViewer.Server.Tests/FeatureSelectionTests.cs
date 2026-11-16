using FluxorViewer.Server.Components;

namespace FluxorViewer.Server.Tests;

// UI-2, UI-13: which Feature the picker lists and selects, with the pick remembered per Host app.
public class FeatureSelectionTests
{
    private readonly FeatureSelection selection = new();

    [Fact]
    public void Features_are_listed_alphabetically_ignoring_case()
    {
        selection.Update("MyApp", ["ZooState", "authState", "Counter"]);

        Assert.Equal(["authState", "Counter", "ZooState"], selection.Features);
    }

    [Fact]
    public void Without_a_pick_the_first_Feature_is_selected()
    {
        selection.Update("MyApp", ["Counter", "Auth"]);

        Assert.Equal("Auth", selection.Selected);
    }

    [Fact]
    public void Nothing_is_selected_without_Features()
    {
        selection.Update("MyApp", ["Counter"]);
        selection.Update("MyApp", []);

        Assert.Empty(selection.Features);
        Assert.Null(selection.Selected);
    }

    [Fact]
    public void The_pick_stays_selected_in_another_Session_that_has_it()
    {
        selection.Update("MyApp", ["Auth", "Counter"]);
        selection.Select("MyApp", "Counter");

        selection.Update("MyApp", ["Auth", "Counter", "Weather"]);

        Assert.Equal("Counter", selection.Selected);
    }

    [Fact]
    public void A_Session_without_the_pick_falls_back_to_its_first_Feature()
    {
        selection.Update("MyApp", ["Auth", "Counter"]);
        selection.Select("MyApp", "Counter");

        selection.Update("MyApp", ["Weather", "Basket"]);

        Assert.Equal("Basket", selection.Selected);
    }

    [Fact]
    public void A_fallback_does_not_overwrite_the_pick()
    {
        selection.Update("MyApp", ["Auth", "Counter"]);
        selection.Select("MyApp", "Counter");
        selection.Update("MyApp", ["Basket"]);

        selection.Update("MyApp", ["Auth", "Counter"]);

        Assert.Equal("Counter", selection.Selected);
    }

    [Fact]
    public void The_pick_is_matched_by_exact_name()
    {
        selection.Update("MyApp", ["Counter"]);
        selection.Select("MyApp", "Counter");

        selection.Update("MyApp", ["Auth", "counter"]);

        Assert.Equal("Auth", selection.Selected);
    }

    [Fact]
    public void Each_Host_app_keeps_its_own_pick()
    {
        selection.Update("ShopApp", ["Auth", "Basket", "Counter"]);
        selection.Select("ShopApp", "Basket");
        selection.Update("AdminApp", ["Auth", "Basket", "Counter"]);
        selection.Select("AdminApp", "Counter");

        selection.Update("ShopApp", ["Auth", "Basket", "Counter"]);

        Assert.Equal("Basket", selection.Selected);
    }

    [Fact]
    public void A_stored_pick_is_selected_for_its_Host_app()
    {
        selection.Restore(new Dictionary<string, string> { ["ShopApp"] = "Basket", ["AdminApp"] = "Counter" });

        selection.Update("ShopApp", ["Auth", "Basket", "Counter"]);

        Assert.Equal("Basket", selection.Selected);
    }
}
