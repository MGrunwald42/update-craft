# update-craft

A command-line tool for Windows that keeps local builds of the ArtCraft apps
(FilmCraft, PdfCraft, PhotoCraft, VectorCraft) up to date: it **safety-checks** each
repository's GitHub issues and pull requests, **pulls** the latest source, **builds** it
with Cargo and **starts** the app.

## Safety check

Before pulling, the updater fetches every issue and merged pull request on
`github.com/storytold/<app>` that was opened or updated since your last pull, together with
all comments written in that window. Unmerged pull requests are skipped, because their code is
not on the branch being pulled. Each item is classified by
[TypeSafe](https://docs.typesafe.ai)'s Jev model for:

| Signal | Meaning |
|---|---|
| Malicious | reports or suspicion of backdoors, tampered code, compromised releases or dependencies |
| Security | vulnerabilities and security-relevant changes |
| Severity 0–3 | how dangerous pulling, building and running the source would be |

Items are flagged **SUSPICIOUS** (malicious ≥ 50 % or severity ≥ 2.5) or **SECURITY**
(security ≥ 60 % with severity ≥ 1, or severity ≥ 1.5). Flagged items are listed with links,
and you decide whether to continue; a suspicious finding defaults to *no*.

The title, text, labels, authors and recent comments of each scanned item (all public on
GitHub) are sent to the TypeSafe API for classification; nothing from your machine is.

## Usage

```powershell
update-craft                          # interactive menu
update-craft update <app|all> [--run] # safety check → git pull → cargo build (→ start)
update-craft check  <app|all>         # safety check only
update-craft build  <app|all>         # cargo build --release only (app + CLI)
update-craft run    <app> [-- args]   # start a built app
update-craft status                   # state of every app
```

When several apps are updated together, every safety check runs first, you choose once which
apps to update, then all pulls and all builds run without further questions.

Every build produces the desktop app and its headless CLI in one Cargo run
(`cargo build --release -p <app> -p <app>-cli`), so the CLI, and the MCP server it serves with
`<app>-cli mcp`, always matches the safety-checked source. Both land in `<app>\target\release\`,
and `status` shows when each was last built.

Options: `--root <dir>` (folder holding the app repositories; default: the exe's folder or the
first parent containing one), `--since-days <n>` (scan a fixed window instead of "since the
last pull"), `--skip-check`, `--env-file <file>` (a `.env` file holding the GitHub token, see
below).

## Requirements and setup

- .NET 10 SDK (to build) / runtime (to run), Git, and the Rust toolchain (`cargo`) for the apps
- `TYPESAFE_API_KEY`: required for the safety check
- `TYPESAFE_API_KEY` can be set in a `.env` file passed with `--env-file`; that value takes
  precedence. Otherwise it is read from the process environment or, as a fallback, from the saved
  Windows user environment, so terminals opened before the variable was set still find it.
- GitHub token: optional but recommended; a fine-grained token with *Public repositories
  (read-only)* access raises GitHub's limit from 60 to 5,000 requests per hour. Put it in a
  `.env` file and pass that with `--env-file`; without it GitHub is queried anonymously:

  ```powershell
  # github.env
  GITHUB_TOKEN=github_pat_...

  update-craft update all --env-file github.env
  ```

The time of each app's last pull is kept in `%LOCALAPPDATA%\update-craft\state.json`.

## Keypad button

[`keypad-plugin\`](keypad-plugin/README.md) puts update-craft on a key of the Logitech MX Creative Keypad: pressing
it starts update-craft, and the key shows the safety check, pull and build progress until the run is done.
`.\keypad-plugin\setup.ps1` sets it up. update-craft works the same without it; it only writes its progress to
`%LOCALAPPDATA%\update-craft\progress.json`.

## Build and install

```powershell
.\publish.ps1            # builds a single-file update-craft.exe into the parent folder
.\publish.ps1 -Destination C:\Tools
```

## License

MIT, see [LICENSE](LICENSE). update-craft uses [Spectre.Console](https://github.com/spectreconsole/spectre.console)
(MIT), see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). Both files are placed next to
the exe in `publish\`; include them when you share the exe.

## Trademarks

update-craft is an independent project, not affiliated with, endorsed by or supported by the
ArtCraft Team. ArtCraft, FilmCraft, PdfCraft, PhotoCraft and VectorCraft are names of the
ArtCraft Team's apps; their name and marks are trademarks of the ArtCraft Team and are used
here only to say which apps this tool works with.
