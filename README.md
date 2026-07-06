# ReDows

> **Know exactly what to keep before you wipe your PC.**
> ReDows scans a Windows 11 machine and builds an exhaustive inventory — apps, configuration and
> personal files — **before a hard reset**, so nothing worth keeping is forgotten.

![Pre-release v0.1.0](https://img.shields.io/github/v/release/hess0ul/ReDows?include_prereleases&label=pre-release&color=orange)
![Windows 11](https://img.shields.io/badge/Windows-11-0078D6?logo=windows11&logoColor=white)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![Read-only](https://img.shields.io/badge/source-read--only-success)

> ℹ️ **This is a v0.1.0 pre-release.** It works — you can download it and use it today — but it is still
> evolving: commands, output and the rule set can change, and parts of the plan are not built yet.
> Feedback is welcome.

---

## ⬇️ Download

Grab the latest build from the **[Releases page](https://github.com/hess0ul/ReDows/releases/latest)**:

1. Download `ReDows-v0.1.0-win-x64.zip`.
2. Unzip it anywhere.
3. Double-click **`redows-gui.exe`**.

No installation and no .NET runtime required — everything is bundled in the single executable. Keep the
`rules`, `modules`, `prescreen`, `memory` and `settings` folders next to it; the app reads them at startup.

Some features (recovering locked files from a volume snapshot, reading machine-wide settings) work better
if you run it as administrator.

## ✨ What it does

- 🧭 **Forget nothing** — the guiding metric: never lose a useful file, config or app.
- 🔒 **Read-only on your PC** — ReDows only reads and copies; it never deletes or modifies anything on the
  scanned machine.
- 🖥️ **A window, or the command line** — a desktop app (`redows-gui.exe`) walks you through it; a CLI
  (`redows.exe`) does the same for power users and scripts. Both run on the same engine.
- 🧹 **Scan and sort** — classify every file (keep / review / ignore) with a completeness report, then
  sort what is left to review folder by folder. Everything is kept by default; you drop the junk into a
  trash you can restore from.
- 💾 **Real backup** — copy what you keep to a disk, USB drive or network share, with secrets sealed in an
  AES-256 vault you can open with your password on any PC. Locked files are recovered from a read-only
  volume snapshot.
- ♻️ **Restore** — put everything back after the reset, to the original locations or a folder you pick;
  secrets are extracted from the vault, nothing is overwritten.
- 🧩 **Total accounting** — every scanned object lands in exactly one bucket (ignore / capture / review),
  with the equation shown — no silent gaps. Anything uncertain goes to review, never to a silent ignore.
- 🔁 **Pairs with InDows** — export your installed apps and settings so
  [InDows](https://github.com/hess0ul/InDows) can reinstall and re-apply them on a fresh Windows.
- 🧠 **Optional local AI** — point it at a local model (e.g. LM Studio or Ollama) for sorting suggestions.
  Off by default, metadata only — nothing leaves your PC.
- 🎮 **Game saves** — an optional catalog (ludusavi) locates game-save folders so they are captured too.
- 🔁 **Find duplicates** — spot duplicate files and how much space you would reclaim (it proposes, never
  deletes).

## 🔧 How it works

1. **Scan** — walk the machine read-only and classify every file via the rule set (ignore / capture /
   review), with a completeness report and an optional per-file manifest of everything worth keeping.
2. **Review and sort** — walk the folders left to review, keep everything by default, and drop what you
   do not need (restorable from the trash).
3. **Back up** — copy what you keep to your chosen destination, sealing any secrets in the encrypted vault
   and recovering locked files from a volume snapshot.
4. **Restore** — after the reset, put the files back and unseal the vault.
5. **Hand off to InDows** — export an InDows-ready profile (apps + settings) to rebuild the machine.

## 🧩 Data-driven by design

What to keep or skip lives in a versioned **YAML rule set** (`rules/`), not in the code, so it is
extensible without recompiling. Category modules (`modules/`) and the folder memory (`memory/`) ride
along the same way.

## 🛠️ Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows 11.

```powershell
git clone https://github.com/hess0ul/ReDows.git
cd ReDows

# desktop app
dotnet run --project src/ReDows.Gui

# or the CLI
dotnet run --project src/ReDows.Cli -- context show      # discover this machine's scan context
dotnet run --project src/ReDows.Cli -- scan --out scan-report.txt   # walk and classify (read-only)
dotnet run --project src/ReDows.Cli -- apps --enrich-winget         # inventory installed apps
dotnet run --project src/ReDows.Cli -- profile --from artifacts/apps.json --out profile   # InDows profile
```

Build a self-contained single-file release (no .NET needed on the target PC):

```powershell
dotnet publish src/ReDows.Gui/ReDows.Gui.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

## 📊 Status

**v0.1.0 pre-release.** Working today: machine-context discovery; a read-only scan with a completeness
report and a per-file keep manifest; the YAML rule set; the installed-apps inventory (with winget
correlation); reading Windows settings grouped by the InDows module that re-applies them; finding
registry-only app secrets (locations only, never values); the desktop app (scan, review-and-sort, backup,
restore); backup to disk/USB/network with an AES-256 secrets vault and locked-file recovery via a volume
snapshot; duplicate detection; an optional local AI sorting assistant; a game-save catalog; and exporting
a complete InDows-ready profile.

Still evolving: more backup destinations (FTP / web / cloud), reading other user accounts' offline hives,
richer correlation between secrets and installed apps, and general polish. Formats and rules may still
change before 1.0.

## 📄 License

Not chosen yet — a `LICENSE` file will be added. Until then, all rights reserved.
