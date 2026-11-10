# Under InteractiveAuto, the Server and WebAssembly stores are separate Sessions

With Blazor InteractiveAuto one browser tab can host a Server-circuit Fluxor store and later a WebAssembly one. We show each as its own Session (labelled with its runtime) rather than correlating them into one, because correlation needs a shared browser-tab id via JS interop and `sessionStorage` and the two stores have genuinely independent histories anyway. Seeing "the same tab" twice in the Session list is expected.
