using Bunit;

namespace FluxorViewer.Server.Tests;

/// <summary>The Home page's browser-side modules, recorded by bUnit rather than run.</summary>
internal static class PaneModules
{
    /// <summary>
    /// Sets up the Action list's scrolling and the Diff tab's rendering (jsondiffpatch runs in the browser), the Diff
    /// reporting that it showed a difference. Returns both modules, for tests that check the calls.
    /// </summary>
    public static (BunitJSModuleInterop Scroll, BunitJSModuleInterop Diff) SetupPaneModules(this BunitJSInterop js)
    {
        var scroll = js.SetupModule("./Components/ActionListPane.razor.js");
        scroll.SetupVoid("scrollToBottom", _ => true);
        var diff = js.SetupModule("./Components/DiffPane.razor.js");
        diff.Setup<bool>("render", _ => true).SetResult(true);
        return (scroll, diff);
    }
}
