// UI-13: the Feature picked for each Host app, kept in localStorage so it outlives the Viewer tab and the Viewer.
const key = "fluxorViewer.featurePicks";

// The stored picks as { appName: featureName }; none when storage is blocked or holds something else.
export function loadFeaturePicks() {
    try {
        const stored = JSON.parse(localStorage.getItem(key) ?? "{}");
        if (typeof stored !== "object" || stored === null || Array.isArray(stored))
            return {};
        return Object.fromEntries(Object.entries(stored).filter(([, feature]) => typeof feature === "string"));
    } catch {
        return {};
    }
}

// Stores one Host app's pick, leaving the others; quietly does nothing when storage is blocked or full.
export function saveFeaturePick(appName, featureName) {
    try {
        localStorage.setItem(key, JSON.stringify({ ...loadFeaturePicks(), [appName]: featureName }));
    } catch {
        // The pick is still kept in memory for this Viewer tab.
    }
}
