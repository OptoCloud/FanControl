# Security guide

vigil runs as root on a host whose fans it controls, and it reads a database that holds every
temperature, drive and power reading of that host. The threat model is modest (a home network,
no multi-tenancy, no untrusted users), but the blast radius of the root daemon is not, so the
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
             │     ├──> Postgres: owner role writes,          │
             │     │    SELECT-only role reads                │
             │     ├──> ntfy (HTTPS, outbound only)           │
             │     └── :3000 on 0.0.0.0, NO AUTH, GET only ───┼──> LAN
             └────────────────────────────────────────────────┘
```

| Boundary | Who may cross | Authentication |
|---|---|---|
| sysfs PWM, `/dev/mpt3ctl` | vigild only, as root | filesystem (root-owned, mode 644) |
| vigild's status socket | any local consumer with group access | unix permissions: mode, uid, gid from `vigild.toml` |
| upsd | vigil-core | none needed: NUT reads are anonymous |
| Postgres | vigil-core | two roles, each with a password in the env file (§3.4) |
| vigil-core HTTP: the dashboard and `/api/*` | **anything on the LAN** | **none: see §2** |

### 1.1 Every listener states its bind address and who authenticates it

A new listener is not added without a row in the table above. If the answer to "who
authenticates this" is "nothing", then nothing it serves may change anything (§2).

vigil-core has exactly one listener (ADR-017). `CORE_HOST` must be an IP address: Kestrel binds
every interface for any other name, so a typo would not fail, it would listen somewhere other
than intended.

*Enforcer:* review checklist; `VigilOptionsValidator` refuses a `CORE_HOST` that is not an
address, and `ListenAddressTests` pins it.

## 2. vigil-core is deliberately LAN-open

**Decided, not overlooked** (ADR-008, amended by ADR-017). vigil-core serves the dashboard and
its API on the LAN with no authentication. Anything on the LAN can read the full host state and
the event log. This is accepted because the network is trusted and nothing vigil-core serves
can change anything: every route is a `GET`.

Authentication is a **planned future feature**, recorded in `docs/DECISIONS.md` and in the
README's status section. There is deliberately no second, loopback-only listener to hide actions
behind: the first action is the point at which authentication arrives.

Two things void the decision, and each is a hard gate:

1. any route that is not a `GET`, or a `GET` that writes, acts or mutates;
2. the host being reachable from outside the LAN.

*Enforcer:* `ApiTests.EveryRouteIsAGet` fails on a route with any other method. A `GET` that
mutates is the review checklist's job, and a blocking question, not a style note.

## 3. Privilege

### 3.1 vigild is the only component that changes anything

vigil-core reads hardware state and writes only history. vigild writes to sysfs and issues one
`ioctl`. This asymmetry is the main containment property of the design and nothing may erode
it: a feature that needs to change hardware belongs in vigild, behind its config validation,
not in vigil-core.

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
silent omission, because several of these break a component outright and the next person needs
to know that before "fixing" the unit:

| Omitted | Where | What it would break |
|---|---|---|
| `ProtectKernelTunables` | `vigild` | Mounts `/sys` read-only. vigild's whole job is writing `/sys/class/hwmon/*/pwmN`; with it, taking a channel to manual fails at startup and **no fan is ever driven**. |
| `PrivateDevices` | `vigild` | Hides `/dev/mpt3ctl` (the HBA temperature ioctl) and the nvidia device nodes. |
| `MemoryDenyWriteExecute` | `vigild` | Applies to `smartctl` and `nvidia-smi` too: vendor binaries whose memory behaviour is not ours to promise. |
| `MemoryDenyWriteExecute` | `vigil-core` | .NET's JIT writes the machine code it then runs, so the process would not start (ADR-016). |
| `CapabilityBoundingSet` | `vigild` | Runs as root and needs `CAP_CHOWN` for the socket's uid/gid; `smartctl` may need `CAP_SYS_RAWIO` for `SG_IO` on some kernels. Narrowing it blind risks losing fan control or drive health, so it must be established on the target host. |

`RestrictAddressFamilies` is worth setting precisely rather than broadly. vigild gets
`AF_UNIX` alone: its API is a unix socket and there is deliberately no TCP code path in it, so
a future change that opened a port would fail at the sandbox as well as in review.

Check a change with `systemd-analyze verify`, which catches a misspelled directive or an
invalid value without deploying anything.

### 3.4 Database roles match what each path does

vigil-core and the dashboard are one process, so this boundary is no longer between processes:
one address space holds both credentials. What remains worth having is that the read path (the
history and event log queries the browser drives) uses a role that cannot write, so a bug in a
query cannot write, drop or truncate anything. `DATABASE_URL` is the schema owner;
`DATABASE_URL_READONLY` is a role granted `SELECT` only:

```sql
create role vigil_web login password '…';
grant connect on database vigil to vigil_web;
grant usage on schema public to vigil_web;
grant select on all tables in schema public to vigil_web;
alter default privileges in schema public grant select on tables to vigil_web;
```

The role keeps its old name so an existing grant still works. Unset, the read path shares the
owner's role and vigil-core warns once at startup. A code comment saying "read-only from here"
is not an enforcer.

*Enforcer:* `VigilDataSources` routes every read through `Reader`; the startup warning.

## 4. Untrusted input

Everything arriving over a socket, from a child process's stdout, from the database, or from an
env var is untrusted. "Local" is not "trusted": a wedged driver and a corrupt page are the
realistic cases here, not an attacker.

### 4.1 Every read is bounded

| Read | Bound |
|---|---|
| HTTP requests to vigild and vigil-core | hyper's and Kestrel's own header and body limits |
| child process stdout | 1 MiB, then the child is killed (`daemon/src/process.rs`) |
| vigild's response headers, in vigil-core | 16 KiB (`VigildClient`) |
| vigild's stream, in vigil-core | 50s of silence ends it (`IdleTimeoutStream`); **no maximum line length yet** |
| upsd `LIST VAR` response | 10s of silence; 64 KiB per answer (`ReadBudgetStream`), which must begin `BEGIN LIST VAR` |

A read with no bound is a memory-exhaustion path. Add the cap when you add the read, not after.

*Enforcer:* review checklist: "what bounds this read?"

### 4.2 Every concurrent resource is capped

vigild caps consumers with `api.max_clients` (16). vigil-core caps browser streams at
`LiveHub.MaxSubscribers` (64), and drops a stream that falls 256 messages behind rather than
buffering for it. Past a cap, reject with `503` rather than queueing.

*Enforcer:* `LiveHubTests` and `ApiTests.LiveRefusesPastTheCap`.

### 4.3 Parsing never trusts shape

`smart.rs` is the standard: smartctl's exit code is explicitly *not* used as a success signal,
availability is decided by whether the JSON actually contains `smart_status`, and an
out-of-range number leaves one field empty rather than failing the poll. A malformed input
degrades one value; it never takes down a loop or panics.

### 4.4 Config is validated before it is acted on

vigild validates its whole config and refuses to start with every problem listed, before a
single fan is touched. vigil-core validates its environment at startup the same way
(`VigilOptionsValidator`, run by `ValidateOnStart`). Extend this, do not weaken it: a config
value that reaches a syscall, a bind address or a SQL identifier is validated first.

## 5. Subprocesses

vigild runs `smartctl` and `nvidia-smi`. vigil-core runs nothing: ntfy is an HTTPS request now.

- **Never through a shell.** Always `Command::new(program).args(...)` with argv. No `sh -c`, no
  string concatenation of a command line.
- **Always a hard timeout**, and kill the child on expiry: `process.rs` does this because a GPU
  that fell off the bus or a drive in error recovery blocks indefinitely.
- **Always `stdin(Stdio::null())`**, so a tool that prompts cannot hang waiting for input.
- **Paths come from config, not from `PATH`** where it matters (`nvidia_smi_path`,
  `smartctl_path`), so a `PATH` entry cannot substitute a different binary.
- **Bound the output** (§4.1).

*Enforcer:* `scripts/check.sh` greps for shell invocation; review checklist.

## 6. SQL

- Values are **always** bound parameters.
- Identifiers may be interpolated only from a compile-time constant or an exhaustive switch over
  a closed set, with a `sql-literal-ok:` comment saying which. `HistoryQueries` derives a table
  name from a validated `RangeKey`; `SchemaSetup` derives every name from its `AllSeries` table.
- Every statement is bounded: 15s (`Limits.DatabaseStatementTimeout`), except the schema
  setup's one-time work, which can legitimately take minutes. An unbounded query on the shared
  Postgres is a denial of service against the host's own monitoring.
- A refresh over a window whose source rows have been dropped by retention **deletes** the
  rollups for that window, which are then the only copy left. `SchemaSetup` documents this;
  treat retention and refresh windows as a correctness-critical pair.

*Enforcer:* `scripts/check.sh` fails on an interpolated SQL string in `core/Vigil.Core/Data`
with no `sql-literal-ok:` reason, and checks the statement timeout is set.

## 7. Secrets

- `DATABASE_URL` and `DATABASE_URL_READONLY` carry passwords. They are never logged, never in
  an error message, never in an HTTP response, and never committed. `PostgresUri` redacts them
  from its own errors.
- A private ntfy topic's name is its only secret, so `NTFY_URL` is treated the same way.
- upsd publishes each driver's configuration as `driver.parameter.*`, credentials included
  (snmp-ups's community and SNMPv3 passwords, the network drivers' logins), and the dashboard
  shows every variable. `NutProtocol` masks them as they are parsed, so nothing downstream can
  serve one; `NutProtocolTests` pins which names count.
- Secrets reach a process only through its systemd `EnvironmentFile`, `/etc/vigil-core.env`,
  which is `0640 root:vigil` and lives outside the repo.
- `.env.example` files hold placeholders and nothing real. `.env` is gitignored in `core/`.
- The dev Postgres password (`vigil`) is for `dev/docker-compose.yml` only, which binds
  `127.0.0.1:5433`. It never appears in a deploy file.

*Enforcer:* `scripts/check.sh` greps for committed credentials; review checklist.

## 8. The browser

- vigil-core sends `X-Content-Type-Options`, `Referrer-Policy`, `X-Frame-Options`,
  `Cross-Origin-Opener-Policy`, `Permissions-Policy`, and the CSP directives a `<meta>` tag
  cannot carry (`frame-ancestors`, `object-src`, `base-uri`, `form-action`), on every response.
- The script and style policy is the static build's own `<meta>` CSP, which SvelteKit writes in
  hash mode with the hash of each inline script it emits. A header `script-src` would be
  enforced alongside it and block those scripts, which is why the header does not carry one.
- No inline script of ours: the theme resolver is `static/theme.js`. No external script, style
  or font origins: vigil-core ships everything it serves.
- No `{@html}` with anything not from a literal in our source.

*Enforcer:* `ApiTests.HealthAnswersWithEverySecurityHeader`; `scripts/check.sh` checks the
headers and the build's hash mode are both still there.

## 9. Dependencies

- A new dependency needs a line in `docs/DECISIONS.md` saying what it replaces and why writing
  it ourselves is worse.
- **No TLS stack** in vigild: it needs C and would end the static musl cross-build (ADR-003).
  vigil-core is .NET, where HTTPS is part of the platform.
- `cargo-deny` gates vigild's advisories and licences; `npm audit` gates the web tree.
- Lockfiles are committed and updated deliberately, never as a side effect.

*Enforcer:* `cargo deny check` and `npm audit` in `scripts/check.sh`.

## 10. Pre-merge checklist

Blocking questions. Any "I don't know" stops the merge.

- [ ] Does this add a listener? What address does it bind, and who authenticates it?
- [ ] Does this add a route that is not a `GET`, or a `GET` that changes anything? (If yes, §2's decision is void: stop.)
- [ ] Does this add a read? What bounds it?
- [ ] Does this add a concurrent resource? What caps it?
- [ ] Does this build SQL? Are all values bound? Where did any identifier come from?
- [ ] Does this spawn a process? Timeout, argv not shell, null stdin, bounded output?
- [ ] Does this widen vigild's privilege, or move a write out of vigild?
- [ ] Could any new log line, error or response contain a credential or a connection URL?
- [ ] Does this add a dependency? Is it in `docs/DECISIONS.md`? Does it pull TLS into vigild?
- [ ] Does this add `unsafe`? Is there a `// SAFETY:` line stating the invariant?
- [ ] Does this change a systemd unit? Is the hardening baseline intact?
