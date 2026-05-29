# OptiSYS — Project Guide

> This builds on the global `~/.claude/CLAUDE.md` (Think Before Coding · Simplicity First ·
> Surgical Changes · Goal-Driven Execution). Those rules always apply; this file adds only
> what is specific to OptiSYS. Don't duplicate the global rules here.

## North star

OptiSYS must look and feel like a **first-class native Windows app**. UI/UX is a top
priority, not an afterthought. The quality bar:

- **Fluent / WinUI 3 native** — prefer stock WinUI controls over hand-rolled chrome.
- **Theme- and dark-mode aware**; use Mica / system backdrop where appropriate.
- **Correct DPI scaling** on mixed-DPI multi-monitor setups.
- **Accessible** — keyboard navigable, screen-reader labels, sensible focus order.
- **Snappy** — never block the UI thread; do work off-thread and marshal back.

When in doubt on UI, lean on the platform (Fluent motion, theming, spacing) rather than
inventing. Restraint keeps `MainWindow.xaml.cs` from sprawling further.

## Orientation

| Project | Role |
|---|---|
| `OptiSYS.Core` | Platform logic — the testable heart. **`IOptimizationDomain` is the spine**: capture baseline → apply → revert → snapshot. `Native/WindowsNativeBridge` is the only P/Invoke layer (Zig is gone). |
| `OptiSYS.App` | WinUI 3 **single-window** UI + runtime/tray/automation services, composed via DI in `AppHost`. |
| `OptiSYS.Tests` | xUnit + Moq + FluentAssertions. Folder layout **mirrors source 1:1**. |
| `installer/` | Inno Setup packaging — builds the native `setup.exe`. Not a .NET project; driven by `build-installer.ps1`. |

New optimizations are new **domains** implementing `IOptimizationDomain`, registered in
`AppHost` in deterministic order — not ad-hoc logic in the UI.

## Build / test / run

⚠️ **Stop any running OptiSYS instance before building** — a live instance locks
`OptiSYS.exe` and the build fails (`MSB3027`).

```powershell
Get-Process OptiSYS -ErrorAction SilentlyContinue | Stop-Process -Force   # do this first
dotnet build src/OptiSYS.sln -c Debug
dotnet test  src/OptiSYS.sln -c Debug
dotnet run   --project src/OptiSYS.App
```

`tools/verify.ps1` is the local verification helper.

## Testing discipline

- **Strict TDD for Core / domain logic**: write the failing test first, then make it pass.
- **`build` + `test` must be green before anything is "done."** No exceptions on Core.
- UI-glue-only changes may skip a unit test, but must be verified by **running the app**.
- Tests live under the mirrored folder: `Tests/Domains`, `Tests/Services`, `Tests/App`.

## Working model

- **Mostly autonomous**: given clear success criteria, loop (build → test → verify) and
  **check in at milestones**, not every step.
- **Non-trivial or any UI/UX work**: brainstorm → design → approval → plan → implement.
  Never skip the design gate for UI.

## Git

- **Trunk-based on `master`.** Small/trivial changes commit straight to `master`.
- **Non-trivial or risky work** (e.g. UI overhauls) gets a short-lived `feat/<topic>`
  branch, merged back to `master` once green.
- Mark milestones with **git tags**. There is no `main` and **no remote** — local only.
- **Conventional commits**: `feat(scope): …`, `fix(scope): …`, `chore: …`.
- **Never commit build output** (`installer/publish/`, `installer/dist/`)
  or `.bak` / `.custom` scratch — already in `.gitignore`.
