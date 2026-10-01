# vigil

Looks after the hardware of a Linux (Proxmox) NAS host. `vigild` (root, `daemon/`) drives the
fans and reads every sensor; `vigil-core` (`core/`) records history and alerts; `vigil-web`
(`web/`) shows it. See `README.md` for what it does and `docs/ARCHITECTURE.md` for how it fits
together.

## Before you change anything

Read `docs/STYLE.md` and `docs/SECURITY.md`. They are short, they are specific to this
codebase, and every rule in them names what enforces it. `docs/DECISIONS.md` records choices
that look arbitrary but are not — check it before "simplifying" one.

`docs/CLEANUP.md` is the live work plan. Phases are reviewed one at a time; do not start the
next one unasked.

## Commands

```bash
scripts/check.sh                                          # everything CI runs. Before every commit.
cargo test --workspace                                    # unit tests, any OS
cargo build --release --target x86_64-unknown-linux-musl   # static vigild and vigil-core
cd web && npm run dev                                      # dashboard against a running vigil-core
```

Developing without the hardware: `dev/mock-vigild.mjs`, `dev/mock-upsd.mjs` and
`dev/docker-compose.yml` (Postgres + TimescaleDB). See README's Development section.

## Invariants

Breaking one of these is a design change, not a refactor. Full list in `docs/ARCHITECTURE.md`.

1. **vigild is the only component that changes hardware.** Consumers read. A feature that needs
   to change hardware belongs in vigild, behind its config validation.
2. **A fan channel is never left on manual PWM.** Four independent layers guarantee it. This is
   why release builds use `panic = "unwind"` — an abort would skip `FanSafetyGuard`'s `Drop`.
3. **An unreadable sensor is never treated as cold.**
4. **vigild keeps no history**; vigil-core does. **vigil-web writes nothing.**
5. **The live view works without a database.** A database failure is retried, never fatal.
6. **No TLS stack and no async runtime in our code** (ADR-003, ADR-004).

## The short version of the style guide

- Shared logic lives in a crate named for its job. Never `common`/`utils`/`helpers`.
- No raw HTTP bytes or SQL text at a call site. Framing has one writer; SQL lives in `.sql`.
- SQL values are always bound. Identifiers only from a compile-time constant or a validated
  closed set.
- Name a number when it encodes a relationship something else depends on. `daemon/src/mpt3.rs`
  is the standard.
- Comments say **why**. Stale comments are worse than none.
- Soft ceiling 400 lines per file, 60 per function. Over it: split, or say why not.
- Tests need no hardware, no root, no network. Fake the boundary.
- Wire types change in `protocol/` and `web/src/lib/types.ts` together, enforced by
  `protocol/contract.json`.

## The short version of the security guide

- vigild listens on a unix socket only. vigil-core binds loopback only.
- **vigil-web is deliberately LAN-open with no auth (ADR-008).** It stays that way *only* while
  it has no mutating route. Adding one voids the decision — stop and raise it.
- Every read is bounded; every concurrent resource is capped.
- Subprocesses: argv not shell, hard timeout, null stdin, paths from config.
- `DATABASE_URL` holds a password. Never log it, never put it in an error or a response.
- Work through the pre-merge checklist at the end of `docs/SECURITY.md`.
