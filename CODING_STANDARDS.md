# Coding standards

How code and tests in this repo should be written. It follows *Clean Code* (Robert C. Martin), adapted to C# and to this codebase. Reviewers cite rules by ID (e.g. **F2**).

**Scope.** Apply the rules to code the change adds or edits. Report older code only when the change makes it worse, for example by adding a fifth parameter or another level of nesting to a long method.

**Severity.** **Must** rules are hard: report a breach as a violation. **Prefer** rules are judgement calls: report a breach with its trade-off, and accept a short reason in the code or commit for keeping it.

Tooling already enforces nullable reference types, warnings as errors and central package versions, so reviewers skip those.

## Names

- **N1 — Domain language (Must).** Name types, members, tests, comments and UI text with the terms in [GLOSSARY.md](GLOSSARY.md), and never with a term from a word's _Avoid_ list. A new domain concept gets its glossary entry in the same change.
- **N2 — Intention-revealing (Must).** A name says what something is or does, so the reader never needs its body to know. Use a full word over an abbreviation (`session`, not `s`), except for loop indices and lambda parameters in a one-line expression.
- **N3 — Parts of speech (Prefer).** Types and properties are nouns (`HistoryLimit`, `LastSequence`). Methods are verbs (`Append`, `ClearHistory`), and `bool` members read as a question (`HasDiscarded`, `IsAllowedOrigin`). Event handlers say what happened (`SnapshotReceived`).
- **N4 — One word per concept (Must).** Use one verb for one idea across the codebase, e.g. *discard* for removing History entries. Don't mix in *evict*, *trim* or *drop* for the same thing.

## Functions

- **F1 — Small (Prefer).** A method does one thing and fits on a screen: aim for 20 lines or fewer. Over 30 is a finding. Extract named steps until each method reads as a short paragraph.
- **F2 — One level of abstraction (Must).** A method's statements sit at a single level: either it coordinates named steps, or it does the low-level work of one step. Mixing the two is a breach, for example deciding a Resync's lost count inline next to calls like `session.Append(…)`. Order methods top-down (the step-down rule): a caller comes before the methods it calls.
- **F3 — Few parameters (Must).** Zero to two parameters is normal and three needs a reason. With four or more, the values that travel together become a type (a record).
- **F4 — No flag arguments (Must).** A `bool` parameter that switches behaviour means two methods; split them. Settable component parameters in Razor are exempt: they're state, not flags.
- **F5 — Command-query separation (Prefer).** A method either changes state or answers a question. The `TryX(out …)` pattern and `Record(…) → bool` ("was it appended?") are the accepted exceptions.
- **F6 — No hidden side effects (Must).** A method does only what its name says. If it also raises an event, logs or changes other state, the name or its XML doc says so.
- **F7 — Shallow nesting (Prefer).** Return early with guard clauses. More than two levels of `if`/loop nesting is a finding: extract the inner block.

## Types and modules

- **T1 — Single responsibility (Must).** A type has one reason to change. When a change edits one type for two unrelated reasons, split it.
- **T2 — Small, cohesive types (Prefer).** A type's members use its own state. If a method reaches more into another object than its own, it belongs on that other object.
- **T3 — Deep modules (Prefer).** Hide decisions behind small interfaces, so callers can't skip a rule. For example, appending to a History discards past the limit inside the Session rather than at every call site. Keep collections private and expose read-only views.
- **T4 — Value types for domain values (Prefer).** A domain value with rules or a unit gets a small record (`HistoryLimit`), not a bare `int` or `string` passed around.
- **T5 — Immutability by default (Prefer).** Use records, `init`, `readonly` and immutable collections. Make something mutable only where it really changes over time, and keep that state behind the type that owns it.

## Comments and docs

- **C1 — Code first (Prefer).** Before writing a comment, try to make the code say it with a better name or an extracted method.
- **C2 — Comments explain why (Must).** A comment gives a reason, an intent, a warning or a reference (an ADR or story ID), never a narration of what the next line does. Delete commented-out code.
- **C3 — XML docs on public members (Must).** Every public type and member has a `<summary>` written in domain terms. Document parameters whose meaning the name can't carry, and any exceptions callers must handle.
- **C4 — Docs follow code (Must).** A change to behaviour described in [docs/architecture.md](docs/architecture.md) updates it in the same commit. A decision that is hard to reverse, surprising and a real trade-off gets an ADR in [docs/adr/](docs/adr/).

## Errors and boundaries

- **E1 — Validate at the edge (Must).** Check input from outside (hub messages, configuration, the command line) where it enters. Inside, trust the types and leave out defensive re-checks.
- **E2 — Expected failures aren't exceptions (Prefer).** Use `TryX`, a result or a `null` with a documented meaning for outcomes a caller should handle. Throw only for broken invariants and programmer errors.
- **E3 — Clear messages (Must).** An error a developer sees is one line naming the input and what it must be (`--history must be a whole number of at least 1, not "abc".`). Nobody should have to read a stack trace to understand it.

## Tests

Tests are behaviour specifications (BDD): each reads as an example of what the system does, in the language of the domain.

- **B1 — Names are sentences (Must).** A test name states the behaviour as a sentence in domain terms, with words separated by underscores: `Resyncs_count_toward_the_limit_and_are_discarded_in_order_like_Actions`. It describes an outcome, not a method (`Discard_works` is a breach).
- **B2 — Given / When / Then (Must).** A test body has up to three blocks separated by blank lines: the context, the one action, then the expected outcome. Use no `// Arrange` labels; the blank lines are the labels. A test that goes on to a second action (Clear, then more Actions) treats each step as its own When/Then pair.
- **B3 — One behaviour per test (Prefer).** Several assertions are fine when they describe one outcome. A second, unrelated behaviour gets its own test.
- **B4 — Test at the agreed seams (Must).** Test through the public seams this repo already uses: the real Viewer over SignalR (`Viewer` fixture), `SessionRegistry` called directly, pages rendered in bUnit against a real registry, or the middleware against a real Fluxor store with a fake transport. Never test private members or reach into internals to assert.
- **B5 — Real collaborators, fakes at the boundary (Must).** Use the real objects. Fake only what crosses a process or time boundary: time (`FakeTimeProvider`), the network transport, browser JS (bUnit's `JSInterop`). The repo has no mocking library; adding one needs an ADR.
- **B6 — Independent expected values (Must).** Expected values are literals or worked examples from the spec (`[501 … 1500]`, `"Older entries discarded · …"`), never recomputed the way the code computes them.
- **B7 — No logic in a test (Prefer).** A test has no `if`, `switch` or `try`. Loops and LINQ belong only in helpers that build input. Put shared setup in small, intention-named helpers (`Snapshot()`, `Dispatch(…)`, `Note(page)`) at the top of the class.
- **B8 — Traceable to a story (Must).** A test class starts with a comment naming its story ID and the behaviour it covers (`// IN-6: each Session keeps at most its History limit of entries…`).
- **B9 — Stable selectors (Must).** UI tests find elements by `data-test` attributes or ARIA roles, never by CSS classes or structure.
- **B10 — Deterministic (Must).** Tests don't sleep for a fixed time. Wait on a condition (`Wait.Until`, bUnit's `WaitForAssertion`) or advance a `FakeTimeProvider`.
