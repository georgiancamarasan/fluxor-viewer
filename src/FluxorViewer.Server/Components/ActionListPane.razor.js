// Follow mode: bring the newest row into view. The Virtualize spacers give the list its full height, so the
// bottom is the newest row.
export function scrollToBottom(list) {
    list.scrollTop = list.scrollHeight;
}

// A keyboard move: scroll the least needed to show the row at this index in full. Rows off screen aren't rendered
// (Virtualize), so its position comes from the fixed row height.
export function scrollRowIntoView(list, index) {
    const rowHeight = 28;
    const top = index * rowHeight;
    if (top < list.scrollTop)
        list.scrollTop = top;
    else if (top + rowHeight > list.scrollTop + list.clientHeight)
        list.scrollTop = top + rowHeight - list.clientHeight;
}

// UI-9: ↑/↓/Home/End move the selection on the server, so the browser mustn't also scroll the list for them. Only these
// keys: Tab, PageUp/PageDown and the rest keep their native behaviour.
const navigationKeys = new Set(["ArrowUp", "ArrowDown", "Home", "End"]);

export function stopNativeScrolling(list) {
    list.addEventListener("keydown", event => {
        if (navigationKeys.has(event.key))
            event.preventDefault();
    });
}
