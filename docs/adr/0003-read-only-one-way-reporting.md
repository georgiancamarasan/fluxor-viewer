# The Viewer is read-only; the middleware never accepts commands

Data flows one way: middleware → Viewer. No time-travel, replay, or dispatching Actions from the Viewer. This keeps the middleware a passive observer that cannot change host-app behaviour and keeps the protocol trivial. Adding interactive features later means introducing a Viewer → middleware channel and should be a new ADR, not an incremental tweak.
