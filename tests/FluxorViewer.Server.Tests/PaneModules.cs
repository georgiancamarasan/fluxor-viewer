using Bunit;

namespace FluxorViewer.Server.Tests;

/// <summary>The Home page's browser-side modules, recorded by bUnit rather than run.</summary>
internal static class PaneModules
{
    /// <summary>
    /// Sets up the Action list's scrolling (following, keyboard moves, and keeping the navigation keys from scrolling it
    /// natively) and the Diff tab's rendering (jsondiffpatch runs in the browser), the Diff reporting that it showed a
    /// difference, and the Feature picks stored in the browser, of which there are none. Every call completes, so the
    /// pane's later calls aren't stuck behind one. Returns the panes' modules, for tests that check the calls.
    /// </summary>
    public static (BunitJSModuleInterop Scroll, BunitJSModuleInterop Diff) SetupPaneModules(this BunitJSInterop js)
    {
        var scroll = js.SetupModule("./Components/ActionListPane.razor.js");
        scroll.SetupVoid("scrollToBottom", _ => true).SetVoidResult();
        scroll.SetupVoid("scrollRowIntoView", _ => true).SetVoidResult();
        scroll.SetupVoid("stopNativeScrolling", _ => true).SetVoidResult();
        var diff = js.SetupModule("./Components/DiffPane.razor.js");
        diff.Setup<bool>("render", _ => true).SetResult(true);
        js.SetupFeaturePicks();
        return (scroll, diff);
    }

    /// <summary>
    /// Sets up the Feature picks stored in the browser (UI-13): loading returns <paramref name="stored"/> (none when not
    /// given) and saving completes. A later call replaces an earlier one. Returns the module, for tests that check the
    /// calls or settle the load themselves.
    /// </summary>
    public static BunitJSModuleInterop SetupFeaturePicks(this BunitJSInterop js, Dictionary<string, string>? stored = null)
    {
        var picks = js.SetupModule("./Components/Pages/Home.razor.js");
        picks.Setup<Dictionary<string, string>>("loadFeaturePicks", _ => true).SetResult(stored ?? []);
        picks.SetupVoid("saveFeaturePick", _ => true).SetVoidResult();
        return picks;
    }

    /// <summary>
    /// Sets up the Feature picks stored in the browser (UI-13) with the load still pending; the test settles it through the
    /// returned handler. Saving completes.
    /// </summary>
    public static JSRuntimeInvocationHandler<Dictionary<string, string>> SetupLoadingFeaturePicks(this BunitJSInterop js)
    {
        var picks = js.SetupModule("./Components/Pages/Home.razor.js");
        picks.SetupVoid("saveFeaturePick", _ => true).SetVoidResult();
        return picks.Setup<Dictionary<string, string>>("loadFeaturePicks", _ => true);
    }
}
