# Security guide

vigil runs as root on a host whose fans it controls, and it reads a database that holds every
temperature, drive and power reading of that host. The threat model is modest — a home network,
no multi-tenancy, no untrusted users — but the blast radius of the root daemon is not, so the
rules below are about **containment**, not about defending a hostile internet.

Every rule names an **Enforcer**.

## 1. Trust boundaries

```
             ┌─ root, host ──────────────┐
  sysfs PWM  │                           │
  /dev/mpt3ctl ─> vigild                 │   no network listener at all
  smartctl   │      │                    │
  nvidia-smi │      └─ unix socket ───────────┐  0660, uid/gid from config
             └───────────────────────────┘    │
                                              v
             ┌─ unprivileged LXC ─────────────────────────────┐
             │  vigil-core ──TCP──> upsd (anonymous reads)    │
             │     │  └──> Postgres (owns the schema)         │
             │     └── /live on 127.0.0.1 only                │
             │            │                                   │
             │            v                                   │
             │  vigil-web ──> Postgres (reads only)           │
             │     └── :3000 on 0.0.0.0, NO AUTH ─────────────┼──> LAN
             └────────────────────────────────────────────────┘
```

| Boundary | Who may cross | Authentication |
|---|---|---|
| sysfs PWM, `/dev/mpt3ctl` | vigild only, as root | filesystem (root-owned, mode 644) |
| vigild's status socket | any local consumer with group access | unix permissions: mode, uid, gid from `vigild.toml` |
| vigil-core `/live`, `/health` | vigil-web, same machine | none — loopback bind is the control |
| upsd | vigil-core | none needed: NUT reads are anonymous |
| Postgres | vigil-core (writes), vigil-web (reads) | role + password in an env file |
| vigil-web HTTP | **anything on the LAN** | **none — see §2** |

### 1.1 Every listener states its bind address and who authenticates it

A new listener is not added without a row in the table above. If the answer to "who
authenticates this" is "nothing", the answer to "what address does it bind" must be loopback or
a unix socket.

*Enforcer:* review checklist; `core/src/config.rs` refuses a non-loopback bind (Phase 1).

## 2. vigil-web is deliberately LAN-open

**Decided, not overlooked.** vigil-web binds `0.0.0.0:3000` with no authentication. Anything on
the LAN can read the full host state and the event log. This is accepted because the network is
trusted and vigil-web writes nothing — it has no mutating route and no database write
permission.

Authentication is a **planned future feature**, recorded in `docs/DECISIONS.md` as ADR-008 and
in the README's status section.

Three things must change the decision, and each is a hard gate:

1. vigil-web gaining any route that writes, acts or mutates;
2. vigil-core's planned action endpoints landing — vigil-core itself stays loopback-only
   regardless, and vigil-web must not become an unauthenticated proxy to them;
3. the host being reachable from outside the LAN.

*Enforcer:* review checklist — "does this add a mutating route to vigil-web?" is a blocking
question, not a style note.

## 3. Privilege

### 3.1 vigild is the only component that changes anything

vigil-core and vigil-web read. vigild writes to sysfs and issues one `ioctl`. This asymmetry is
the main containment property of the design and nothing may erode it: a feature that needs to
change hardware belongs in vigild, behind its config validation, not in a consumer.

### 3.2 A root daemon does not listen on a port

vigild's API is a unix socket, reachable only by a local process with group access, and a
containerised consumer gets the socket's *directory* bind-mounted in. There is no TCP code path
to misconfigure.

*Enforcer:* `daemon/` has no `TcpListener`; `scripts/check.sh` greps for one.

### 3.3 Every unit takes the hardening baseline, or says why not

The baseline, strongest first:

```ini
NoNewPrivileges=true
ProtectSystem=strict         # "full" only where something genuinely needs a writable path
ProtectHome=true
PrivateTmp=true
PrivateDevices=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
ProtectClock=true
ProtectHostname=true
RestrictAddressFamilies=…    # only the families that component actually uses
RestrictNamespaces=true
RestrictSUIDSGID=true
RestrictRealtime=true
SystemCallArchitectures=native
LockPersonality=true
MemoryDenyWriteExecute=true
CapabilityBoundingSet=       # empty for anything that does not run as root
```

**A directive may be omitted only with a comment in the unit saying why**, in the form
`# <Directive>: NOT set, <reason>`. `scripts/check.sh` accepts the comment and fails on a
silent omission, because three of these break a component outright and the next person needs
to know that before "fixing" the unit:

| Omitted | Where | What it would break |
|---|---|---|
| `ProtectKernelTunables` | `vigild` | Mounts `/sys` read-only. vigild's whole job is writing `/sys/class/hwmon/*/pwmN`; with it, taking a channel to manual fails at startup and **no fan is ever driven**. |
| `PrivateDevices` | `vigild` | Hides `/dev/mpt3ctl` (the HBA temperature ioctl) and the nvidia device nodes. |
| `MemoryDenyWriteExecute` | `vigild` | Applies to `smartctl` and `nvidia-smi` too — vendor binaries whose memory behaviour is not ours to promise. |
| `MemoryDenyWriteExecute` | `vigil-web` | V8 maps its JIT output writable then executable. `node` refuses to start. |
| `CapabilityBoundingSet` | `vigild` | Runs as root and needs `CAP_CHOWN` for the socket's uid/gid; `smartctl` may need `CAP_SYS_RAWIO` for `SG_IO` on some kernels. Narrowing it blind risks losing fan control or drive health, so it must be established on the target host. |

`RestrictAddressFamilies` is worth setting precisely rather than broadly. vigild gets
`AF_UNIX` alone: its API is a unix socket and there is deliberately no TCP code path in it, so
a future change that opened a port would fail at the sandbox as well as in review.

Check a change with `systemd-analyze verify ./deploy/vigild.service`, which catches a
misspelled directive or an invalid value without deploying anything.

### 3.4 Database roles match what each component does

vigil-core owns the schema and writes. vigil-web reads. They must not share a role: vigil-web
connects as `vigil_web`, granted `SELECT` only.

```sql
create role vigil_web login password '…';
grant connect on database vigil to vigil_web;
grant usage on schema public to vigil_web;
grant select on all tables in schema public to vigil_web;
alter default privileges in schema public grant select on tables to vigil_web;
```

The current single-role setup is a Phase 1 fix. A code comment saying "read-only from here" is
not an enforcer.

## 4. Untrusted input

Everything arriving over a socket, from a child process's stdout, from the database, or from an
env var is untrusted. "Local" is not "trusted" — a wedged driver and a corrupt page are the
realistic cases here, not an attacker.

### 4.1 Every read is bounded

| Read | Bound |
|---|---|
| HTTP request headers | `MAX_REQUEST_BYTES` (8 KiB) |
| child process stdout | **unbounded today — Phase 1 fix** (`daemon/src/process.rs:34`) |
| upsd `LIST VAR` response | per-line via `read_line`; needs a total cap |
| SSE stream from vigil-core | parser buffers until a newline; needs a max line length |

A read with no bound is a memory-exhaustion path. Add the cap when you add the read, not after.

*Enforcer:* review checklist — "what bounds this read?"

### 4.2 Every concurrent resource is capped

vigild caps consumers with `api.max_clients`; vigil-core caps at `MAX_CLIENTS = 16`;
vigil-web's browser subscriber set is **unbounded today** (Phase 1 fix). Past the cap, reject
with `503` rather than queueing.

### 4.3 Parsing never trusts shape

`smart.rs` is the standard: smartctl's exit code is explicitly *not* used as a success signal,
availability is decided by whether the JSON actually contains `smart_status`, and an
out-of-range number leaves one field empty rather than failing the poll. A malformed input
degrades one value; it never takes down a loop or panics.

### 4.4 Config is validated before it is acted on

vigild validates its whole config and refuses to start with every problem listed, before a
single fan is touched. vigil-core validates its env and exits 2. Extend this, do not weaken it:
a config value that reaches a syscall, a bind address or a SQL identifier is validated first.

## 5. Subprocesses

vigil shells out to `smartctl`, `nvidia-smi` and `curl`.

- **Never through a shell.** Always `Command::new(program).args(...)` with argv. No `sh -c`, no
  string concatenation of a command line. This is why passing a UPS event message to `curl`
  as `--data-binary` is safe even with arbitrary text in it.
- **Always a hard timeout**, and kill the child on expiry — `process.rs` does this because a GPU
  that fell off the bus or a drive in error recovery blocks indefinitely.
- **Always `stdin(Stdio::null())`**, so a tool that prompts cannot hang waiting for input.
- **Paths come from config, not from `PATH`** where it matters (`nvidia_smi_path`,
  `smartctl_path`), so a `PATH` entry cannot substitute a different binary.
- **Bound the output** (§4.1).

*Enforcer:* `scripts/check.sh` greps for `sh -c` and shell invocation; review checklist.

## 6. SQL

- Values are **always** bound parameters. See STYLE §3.3 for the one remaining violation.
- Identifiers may be interpolated only from a compile-time constant or an exhaustive match over
  a validated closed set. `web/src/lib/server/history.ts` is correct because `isRangeKey`
  validates before `RANGES` maps to a table name.
- Every connection sets `statement_timeout`. vigil-core sets 15s; vigil-web sets none today
  (Phase 1 fix). An unbounded query on the shared Postgres is a denial of service against the
  host's own monitoring.
- A refresh over a window whose source rows have been dropped by retention **deletes** the
  rollups for that window, which are then the only copy left. `timescale.rs` documents this;
  treat retention and refresh windows as a correctness-critical pair.

## 7. Secrets

- `DATABASE_URL` carries a password. It is never logged, never in an error message, never in an
  HTTP response, and never committed.
- Secrets reach a process only through its systemd `EnvironmentFile`. Those files are
  `0640 root:vigil` and live outside the repo (`/etc/vigil-core.env`, `/etc/vigil-web.env`).
- `.env.example` files hold placeholders (`USER:PASSWORD`) and nothing real. `.env` is
  gitignored in both `core/` and `web/`.
- The dev Postgres password (`vigil`) is for `dev/docker-compose.yml` only, which binds
  `127.0.0.1:5433`. It never appears in a deploy file.

*Enforcer:* `scripts/check.sh` greps staged content for likely secrets; review checklist.

## 8. The browser

- Security headers are set in `web/src/hooks.server.ts`: CSP, `X-Content-Type-Options`,
  `Referrer-Policy`, `X-Frame-Options`. **None exist today** — Phase 1 fix.
- CSP allows no inline script except the theme resolver, which gets a nonce or a hash. No
  external script, style or font origins: vigil-web ships everything it serves.
- No `{@html}` with anything not from a literal in our source.
- vigil-web proxies vigil-core's stream; it does not expose vigil-core directly, and never
  forwards a browser-supplied URL, path or header to it.

## 9. Dependencies

- A new dependency needs a line in `docs/DECISIONS.md` saying what it replaces and why writing
  it ourselves is worse.
- **No TLS stack** in Rust: it needs C and would end the static musl cross-build. ntfy goes
  through `curl` for exactly this reason.
- `cargo-deny` gates advisories and licences; `npm audit` gates the web tree.
- Lockfiles are committed and updated deliberately, never as a side effect.

*Enforcer:* `cargo deny check` and `npm audit` in `scripts/check.sh`.

## 10. Pre-merge checklist

Blocking questions. Any "I don't know" stops the merge.

- [ ] Does this add a listener? What address does it bind, and who authenticates it?
- [ ] Does this add a **mutating** route to vigil-web? (If yes, §2's decision is void — stop.)
- [ ] Does this add a read? What bounds it?
- [ ] Does this add a concurrent resource? What caps it?
- [ ] Does this build SQL? Are all values bound? Where did any identifier come from?
- [ ] Does this spawn a process? Timeout, argv not shell, null stdin, bounded output?
- [ ] Does this widen vigild's privilege, or move a write out of vigild?
- [ ] Could any new log line, error or response contain a credential or a connection URL?
- [ ] Does this add a dependency? Is it in `docs/DECISIONS.md`? Does it pull in TLS or an async runtime?
- [ ] Does this add `unsafe`? Is there a `// SAFETY:` line stating the invariant?
- [ ] Does this change a systemd unit? Is the hardening baseline intact?
