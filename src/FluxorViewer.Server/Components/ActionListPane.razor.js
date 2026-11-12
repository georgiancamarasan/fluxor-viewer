// Follow mode: bring the newest row into view. The Virtualize spacers give the list its full height, so the
// bottom is the newest row.
export function scrollToBottom(list) {
    list.scrollTop = list.scrollHeight;
}
