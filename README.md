# ReDows — In Progress 🚧

> 🚧 **Heads-up — this is a work in progress, not a finished product.** ReDows is under active
> development: commands, output formats and the rule set can change without notice, and parts of the
> plan are not built yet. Use it to explore and give feedback, not (yet) as a backup tool you rely on.

> **Know exactly what to keep before you wipe your PC.**
> ReDows scans a Windows 11 machine and builds an exhaustive inventory — apps, configuration and
> personal files — **before a hard reset**, so nothing worth keeping is forgotten.

![Windows 11](https://img.shields.io/badge/Windows-11-0078D6?logo=windows11&logoColor=white)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![Status: early development](https://img.shields.io/badge/status-early%20development-orange)
![Read-only](https://img.shields.io/badge/source-read--only-success)

---

## ✨ What it does

- 🧭 **Forget nothing** — the guiding metric: never lose a useful file, config or app.
- 🔒 **Read-only** — ReDows only reads and reports; it never deletes or modifies anything on the scanned PC.
- 🧮 **Total accounting** — every scanned object lands in exactly one bucket (ignore / capture / review),
  with the equation shown — no silent gaps.
- 🙋 **Default to review** — anything not classified with certainty goes to a human review queue, never to
  a silent "ignore". The ignore list is an explicit allow-list, not the other way around.
- 🧩 **Data-driven** — what to keep or skip lives in a versioned YAML rule set, not in the code, so it is
  extensible without recompiling.

## 🔁 Pairs with InDows

ReDows is the **before-reset** half of a pair; its sibling **[InDows](https://github.com/hess0ul/InDows)**
is the **after-reset** half that rebuilds a clean Windows install.

> InDows builds a fresh ISO → you use the PC → ReDows captures what mattered → InDows rebuilds a clean,
> similar ISO → and so on. ReDows exports a complete InDows-ready profile — your installed apps as a winget
> `configuration.dsc.yaml`, plus your settings grouped by the InDows module that re-applies them.

## 🔧 How it works

1. **Scan** — walk the machine read-only and classify every file via the rule set (ignore / capture / review),
   and optionally write a per-file manifest of everything worth keeping.
2. **Inventory apps** — enumerate installed software across many sources and, where possible, attach an
   exact winget id for reinstall.
3. **Read settings** — read catalogued Windows settings (Explorer, privacy, power, features…) read-only and
   group them by the InDows module that re-applies them.
4. **Find registry secrets** — inventory app secrets/config that live only in the registry (PuTTY, WinSCP,
   Remote Desktop…) by location only — a secret value is never read.
5. **Export a profile** — emit a complete InDows-ready profile (winget app catalog + settings), closing the
   ReDows → InDows loop.

## 🚀 Quick start

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows 11.

```powershell
git clone https://github.com/hess0ul/ReDows.git
cd ReDows
dotnet build

# discover this machine's scan context
dotnet run --project src/ReDows.Cli -- context show

# walk and classify (read-only), with a completeness report
dotnet run --project src/ReDows.Cli -- scan --out scan-report.txt

# inventory installed apps (add --enrich-winget to attach winget ids; this runs winget)
dotnet run --project src/ReDows.Cli -- apps --out artifacts --enrich-winget

# read catalogued Windows settings (read-only), grouped by the InDows module that re-applies them
dotnet run --project src/ReDows.Cli -- settings --by-module --out artifacts

# inventory registry-only app secrets/config a file scan misses (read-only, locations only)
dotnet run --project src/ReDows.Cli -- secrets

# write the complete InDows-ready profile: apps catalog + settings + a plain-language README
dotnet run --project src/ReDows.Cli -- profile --out profile --from artifacts/apps.json
```

## 📊 Status

Working today: machine context discovery, read-only scan with a completeness report and an optional
per-file keep manifest, the YAML rule set, the installed-apps inventory (with winget correlation),
reading Windows settings and grouping them by InDows module, finding registry-only app secrets
(locations only, never values), and exporting a complete InDows-ready profile (apps + settings).

Planned next: the actual file copy into a destination with an encrypted vault for secrets, reading other
user accounts' settings from offline hives, and richer correlation between secrets and installed apps.

## 📄 License

Not chosen yet — a `LICENSE` file will be added. Until then, all rights reserved.
