// The Diff tab's before → after view. jsondiffpatch is copied into wwwroot/lib by the build (see the .csproj).
import { create } from '../lib/jsondiffpatch/index.js';
import { format } from '../lib/jsondiffpatch/formatters/html.js';

// Array items are matched by Id where they have one, so an insert shows as one added item rather than every item
// after it changing; items without one are matched by position. Strings aren't diffed character by character, since
// no diff-match-patch is passed: a changed string shows old → new in full.
const differ = create({
    objectHash: (item, index) => {
        const id = item !== null && typeof item === 'object' ? (item.Id ?? item.id) : undefined;
        return id === undefined ? `$$index:${index}` : `$$id:${JSON.stringify(id)}`;
    },
});

// Renders the diff into host, which Blazor leaves empty, and returns whether there was any difference. A null before
// means the Feature didn't exist yet, so all of after shows as added. Numbers go through JSON.parse, so differences
// beyond 2^53 are lost: the caller says so when the JSON differed but this finds nothing.
export function render(host, before, after, showUnchanged) {
    const left = before === null ? undefined : JSON.parse(before);
    const right = JSON.parse(after);
    const delta = differ.diff(left, right);
    host.classList.toggle('jsondiffpatch-unchanged-hidden', !showUnchanged);
    host.innerHTML = delta === undefined ? '' : format(delta, left);
    return delta !== undefined;
}
