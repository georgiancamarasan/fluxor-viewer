using FluxorViewer.Server.Components;

namespace FluxorViewer.Server.Tests;

// UI-3: Action types are listed by their short name. The middleware sends readable C# names.
public class ActionTypeNameTests
{
    [Theory]
    [InlineData("MyApp.Store.Counter.IncrementAction", "IncrementAction")]
    [InlineData("IncrementAction", "IncrementAction")]
    [InlineData("MyApp.Items.LoadedAction<MyApp.Item>", "LoadedAction<Item>")]
    [InlineData("MyApp.Outer<System.Int32>.Inner<System.String>", "Inner<String>")]
    [InlineData("System.Collections.Generic.Dictionary<System.String, System.Collections.Generic.List<System.Int32>>", "Dictionary<String, List<Int32>>")]
    [InlineData("MyApp.Loaded<MyApp.Item[]>", "Loaded<Item[]>")]
    [InlineData("MyApp.Loaded<MyApp.Item>[]", "Loaded<Item>[]")]
    [InlineData("System.Int32[,]", "Int32[,]")]
    public void Short_names_drop_namespaces_and_enclosing_types(string fullName, string expected) =>
        Assert.Equal(expected, ActionTypeName.Short(fullName));
}
