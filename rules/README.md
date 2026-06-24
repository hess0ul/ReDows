# rules/ — the ReDows ruleset (the knowledge)

The YAML files in this folder are the **data** that drives the engine: the
deny-list (what can be safely ignored), the capture rules (what must be saved)
and the carve-outs (what must never be swallowed by an ignore). The engine
(`ReDows.Core`) is stable code; the knowledge lives here and evolves without
recompiling.

**A broken ruleset is a data-loss risk, so everything is fail-closed**: unknown
keys, duplicate keys, duplicate ids, invalid verdicts/tokens, or a
`schema_version` newer than the engine all abort loading — the engine refuses
to scan rather than scan wrong.

## Personal vs generic rules

ReDows ships a **generic** ruleset. Machine- or user-specific rules (a particular
game folder, a custom data drive, a niche app on one PC) belong in a `rules/perso/`
subfolder. The loader walks `rules/` recursively, so anything dropped in
`rules/perso/` is picked up automatically — no wiring, same schema and validation.

`rules/perso/` is **kept out of the public release**: it is excluded when the
generic version is published, so personal rules stay in the private copy. This is
the rule-set side of the project's generic-first split — the public catalogue
stays universal, personal additions live beside it without leaking. (Non-`.yaml`
files in `rules/` — like this README — are ignored by the loader.)

## Editing

`ruleset.schema.json` is **generated from the C# DTOs** (single source of
truth) with `redows rules schema --out rules/ruleset.schema.json`; a drift test
fails when it is stale. With the recommended `redhat.vscode-yaml` extension you
get autocompletion, hover documentation and live validation (each file carries
a `# yaml-language-server: $schema=…` modeline; `.vscode/settings.json` maps the
folder too). Validate from the CLI with `redows rules validate`.

## File shape

```yaml
schema_version: 1        # engine refuses newer versions (fail-closed)
layer: deny              # carve_out | deny | capture — stage of every rule in the file
rules:
  - id: dev.terraform            # stable unique id (manifests reference it)
    scope: drive                 # machine | user | volume | drive
    match: '**/.terraform/**'    # bounded glob, see grammar below
    verdict: ignore              # ignore | review | note_only | capture:{user,config,secret}
    prio: normal                 # optional: critical | high | normal | low
    bare_name_class: distinctive # required on floating ignores, see §0-8 policy
    when: …                      # optional context condition
    exceptions: …                # nested rules evaluated before this one
    source: 'vault deny-list v2 §A.5'   # provenance
    note: '…'                    # rationale for humans
```

Files merge into one ruleset; **load order never matters** (precedence is
fully determined by the semantics below). Ids share one global namespace,
including exception ids: one zone, one verdict (§0-9).

## Pattern grammar (normative)

- Separator is `/` (never `\`). Matching is **ordinal, case-insensitive**.
- Segments: literals, wildcards (`*` = zero or more characters, `?` = exactly
  one) and `**` (zero or more whole segments). A trailing `/**` also matches
  the base directory itself.
- **No absolute or localized paths** (generality invariant). The first segment
  must be a location token where the scope requires one:

| Scope | Anchoring | First segment |
| --- | --- | --- |
| `machine` | machine location | `%SystemRoot%`, `%SystemDrive%`, `%ProgramData%`, `%AllUsersProfile%`, `%ProgramFiles%`, `%ProgramFiles(x86)%`, `%ProgramW6432%`, `%Public%` |
| `user` | instantiated **per profile** (ProfileList — never a `Users\*` glob), tokens resolved from each profile's own hive/environment | `<UserProfile>`, `%AppData%`, `%LocalAppData%`, `%Temp%`, or `FOLDERID_X` (Known Folder by GUID — follows redirections) |
| `volume` | root of every volume | any segment except `**` (wildcards allowed: `found.*`, `DumpStack.log*`) |
| `drive` | floating, every volume | must start with `**/` |

Known Folder tokens: `FOLDERID_Desktop`, `FOLDERID_Documents`,
`FOLDERID_Downloads`, `FOLDERID_Pictures`, `FOLDERID_Music`, `FOLDERID_Videos`,
`FOLDERID_SavedGames`, `FOLDERID_Favorites`, `FOLDERID_Contacts`,
`FOLDERID_Links`, `FOLDERID_SavedSearches`, `FOLDERID_CameraRoll`,
`FOLDERID_Screenshots`. Unknown tokens are validation errors. (Public folders
are reachable through the `%Public%` machine token; per-GUID `FOLDERID_Public*`
tokens will return the day a provider resolves them — a token that validates
but never instantiates would be a silent dead rule.)

## Evaluation semantics (normative — the meaning of every rule depends on this)

1. **Stages, in fixed order**: access errors → claimed zones (engine-fed at scan
   time by INDEX_EXTERNE parsers — relocated mail stores, Calibre libraries, VM
   folders; structurally restricted to review/capture, never ignore; ids
   `index:<app>@<binding>`, stage `claimed`) → `carve_out` → `deny` →
   `capture` → **default REVIEW**. The first stage with at least one matching
   rule decides the verdict.
2. **Within a stage, the most specific match wins**: more literal segments
   first, then fewer `**` segments, then fewer wildcard characters.
3. **Ties go to the most conservative verdict** (§0-7):
   `capture:secret` > `capture:user` > `capture:config` > `review` >
   `note_only` > `ignore`. Remaining ties resolve by rule id (determinism).
4. **Exceptions are evaluated before their parent** (§0-9): a matching nested
   exception overrides the parent's verdict, recursively (innermost wins).
   An ignore rule with exceptions is the `IGNORE_EXC` construct of the design
   notes — and it tells the engine the subtree is **not prunable**.
5. **Anything unmatched is REVIEW** (default-to-review invariant): ignoring is
   whitelisted, never inferred.

### Engine-reserved rule ids (stage `engine`)

Some verdicts are decided by code, before the ruleset — they are still counted
items in the report's accounting equation, never silent skips. Their ids are
reserved: the loader rejects any ruleset id equal to `default.review` or
starting with `engine.`.

| Rule id | Verdict | Situation |
| --- | --- | --- |
| `engine.inaccessible` | `review` | a directory that could not be enumerated: one "unknown subtree" item |
| `engine.reparse_point` | `note_only` | a non-traversed reparse directory, or a name-surrogate file (symlink). Reparse-tagged files that are NOT name surrogates (WOF compression, deduplication, cloud placeholders) are real data and flow to the ruleset |
| `engine.orphan_profile` | `review` | anything under a profile directory absent from ProfileList — no ignore rule may touch it |
| `engine.self_output` | `ignore` | ReDows' own output, the single engine whitelist |
| `engine.volume_unmounted` | — | context-level, not per-item: a discovered volume excluded from the walk, listed in the report with its reason |
| `default.review` | `review` | the default stage: matched by no rule |

## Conditions

A `when` block gates a rule: when false, the rule simply does not match.
Composition frame: `all` / `any` (nestable). V1 predicate:

- `ancestor_marker: [globs]` — true when an ancestor directory of the item
  (up to the volume root) contains an entry matching one of the name globs.

Planned predicates (added additively): VCS-ignore lookup, identified-app zones,
content magic numbers.

## Bare-name policy (deny-list §0-8)

A **floating ignore** (`scope: drive` + `verdict: ignore`) must declare
`bare_name_class`:

- `distinctive` — the name cannot reasonably collide (`node_modules`,
  `.terraform`, `__pycache__`).
- `collision_prone` — generic names (`build`, `cache`, `logs`…) **must** carry
  a `when` condition proving context. A bare `**/build/**` ignore without
  context would eat user data named "build" — exactly the regression the v2
  red-team pass fixed.

## Flags (orthogonal axis)

`flags` is a list of orthogonal markers — an axis, never encoded in the verdict.
V1 vocabulary:

- `dpapi_machine_bound` — the captured bytes are DPAPI-bound to this machine and
  will be UNREADABLE after a reset (Signal key, WireGuard `.conf.dpapi`,
  Chromium `Login Data`, RDCMan `.rdg`…). Items classified by such a rule are
  surfaced in the report's **pre-reset alerts**: export or synchronize through
  the application BEFORE wiping — capturing alone is a false sense of safety.

## App templates (apps-ctt catalogue)

Shared per-app patterns are declared once and instantiated per application:

```yaml
schema_version: 1
templates:
  - name: chromium
    params: [vendor_path]
    rules:
      - id_suffix: profile        # expands to app.<name>.profile
        layer: capture            # template rules carry their own layer
        scope: user
        match: '{vendor_path}/User Data/**'
        verdict: capture:config
      - id_suffix: cache
        layer: deny
        scope: user
        match: '{vendor_path}/User Data/**/Cache/**'
        verdict: ignore
apps:
  - name: chrome
    template: chromium
    params: {vendor_path: '%LOCALAPPDATA%/Google/Chrome'}
    source: 'apps-ctt §2'
```

Expansion happens at load time into ordinary rules (the engine never sees
templates). Everything is fail-closed: unknown template, missing or extra
param, undeclared `{placeholder}`, invalid layer — all are load errors. Apps may
reference templates from any file (load order stays irrelevant). Generated ids
(`app.<name>.<suffix>`) share the global id namespace.

## Settings catalog (a separate file family)

`settings/*.yaml` (a sibling of `rules/`) is a different data set, read by `redows
settings`: the list of Windows *settings* ReDows reads from the registry (the
ReDows → InDows loop). It has its own schema (`schema_version` + `settings:` with
`hive`/`key`/`value`/`type`/`decode`/`indows_module`) and its own fail-closed loader
— it does not mix with the classification rules here.

## Evolution policy

The schema evolves **additively**: new optional fields (with defaults), new
condition predicates, new rule kinds. Axes like `prio` (and later `mode` and
`flags`) are attributes, **never encoded in the verdict value**. A file
declaring a newer `schema_version` is refused, never best-effort parsed.
