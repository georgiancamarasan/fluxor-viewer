using Bunit;

namespace FluxorViewer.Server.Tests;

/// <summary>The Home page's browser-side modules, recorded by bUnit rather than run.</summary>
internal static class PageModules
{
    private const string FeaturePicksModule = "./Components/Pages/Home.razor.js";

    /// <summary>
    /// Sets up the Action list's scrolling (following, keyboard moves, and keeping the navigation keys from scrolling it
    /// natively), the Diff tab's rendering (jsondiffpatch runs in the browser), the Diff reporting that it showed a
    /// difference, and the Feature picks stored in the browser, of which there are none. Every call completes, so the
    /// page's later calls aren't stuck behind one. Returns the panes' modules, for tests that check the calls.
    /// </summary>
    public static (BunitJSModuleInterop Scroll, BunitJSModuleInterop Diff) SetupPageModules(this BunitJSInterop js)
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
    /// calls.
    /// </summary>
    public static BunitJSModuleInterop SetupFeaturePicks(this BunitJSInterop js, Dictionary<string, string>? stored = null)
    {
        var picks = js.SetupStoringFeaturePicks();
        picks.Setup<Dictionary<string, string>>("loadFeaturePicks", _ => true).SetResult(stored ?? []);
        return picks;
    }

    /// <summary>
    /// Sets up the Feature picks stored in the browser (UI-13) with the load still pending; the test settles it through the
    /// returned handler. Saving completes.
    /// </summary>
    public static JSRuntimeInvocationHandler<Dictionary<string, string>> SetupLoadingFeaturePicks(this BunitJSInterop js) =>
        js.SetupStoringFeaturePicks().Setup<Dictionary<string, string>>("loadFeaturePicks", _ => true);

    private static BunitJSModuleInterop SetupStoringFeaturePicks(this BunitJSInterop js)
    {
        var picks = js.SetupModule(FeaturePicksModule);
        picks.SetupVoid("saveFeaturePick", _ => true).SetVoidResult();
        return picks;
    }
}
