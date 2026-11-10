# Build our own viewer instead of using Fluxor's Redux DevTools integration

Fluxor ships `Fluxor.Blazor.Web.ReduxDevTools`, which already shows actions and state diffs, but it depends on the Redux DevTools browser extension. Our development machines do not allow installing browser extensions, so we build a standalone viewer (a server the middleware reports to) that needs nothing installed in the browser.
