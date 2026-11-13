using Bunit;

namespace FluxorViewer.Server.Tests;

/// <summary>The Home page's browser-side modules, recorded by bUnit rather than run.</summary>
internal static class PaneModules
{
    /// <summary>
    /// Sets up the Action list's scrolling (following, keyboard moves, and keeping the navigation keys from scrolling it
    /// natively) and the Diff tab's rendering (jsondiffpatch runs in the browser), the Diff reporting that it showed a
    /// difference. Every call completes, so the pane's later calls aren't stuck behind one. Returns both modules, for
    /// tests that check the calls.
    /// </summary>
    public static (BunitJSModuleInterop Scroll, BunitJSModuleInterop Diff) SetupPaneModules(this BunitJSInterop js)
    {
        var scroll = js.SetupModule("./Components/ActionListPane.razor.js");
        scroll.SetupVoid("scrollToBottom", _ => true).SetVoidResult();
        scroll.SetupVoid("scrollRowIntoView", _ => true).SetVoidResult();
        scroll.SetupVoid("stopNativeScrolling", _ => true).SetVoidResult();
        var diff = js.SetupModule("./Components/DiffPane.razor.js");
        diff.Setup<bool>("render", _ => true).SetResult(true);
        return (scroll, diff);
    }
}
