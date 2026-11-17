# Windows CI and releases

The `Windows build, test and package` check builds with the SDK pinned in global.json, runs the unit suite, publishes a self-contained Windows x64 single-file executable with four Whisper sidecar libraries, validates every file after ZIP extraction and runs the extracted executable in isolated package-verification mode. All jobs use Windows runners. Three signed-in live AI tests and the optional local Whisper/audio-fixture test are intentionally skipped without their required authorized profile/fixtures; CI is not a claim that live microphone/AI integrations were tested.

PRs validate without repository write permissions. A push to main repeats validation for that exact source SHA and only then publishes `trayvora-win-x64.zip`, `SHA256SUMS.txt` and `release.json`. No token/PAT secret is needed: only the release job has contents:write. Tag creation does not trigger this workflow. No version commit is pushed, so there is no workflow loop.

Version is `0.1.(first-parent commit count - 1)`, starting with 0.1.1 for the first integration after the initial commit. Main history must remain linear/append-only, with the existing no-force-push protection. Squash/merge each advances one step; a rebase integration may advance multiple patch numbers. Gaps are valid. Each main push has independent jobs (no cancellation queue), so closely spaced merges are not dropped. Published releases and tags are never overwritten; inconsistent retries fail closed. Existing draft assets are downloaded and compared before publication. A late older run is not marked latest.

Manual workflow dispatch runs validation only, never publication. To retry a failed release, rerun the original main push workflow. A build/package rerun that produces different bytes from an already-created draft fails safely and requires investigation instead of silently replacing files.

GitHub rules: default branch requires PR and the Windows check, blocks deletion/force push; release v* tags block update/deletion. No bypass actors. Actions token defaults read-only and cannot approve PRs. Package/test artifacts expire after 7 days; published release assets remain.

Local verification (fresh output folder required):

```powershell
./scripts/ci/build.ps1 -OutputRoot 'E:\Temp\trayvora-ci-check-unique'
```

This performs build/tests/package validation without publishing. No existing output directory is deleted. Native dependencies and the .NET runtime are included; speech models and local user data are not.


## Compact portable package

CI uses `CompactWinX64.pubxml`: managed assemblies and the .NET runtime are bundled into `Trayvora.exe`. Native .NET, Skia and SQLite libraries are bundled for extraction by the .NET host. Trimming and AOT are deliberately disabled to preserve reflection-based Avalonia and application behavior. Debug symbols are embedded rather than shipped as loose PDB files.

A fresh ZIP has four files in its root (`Trayvora.exe`, `release.json`, `START.txt`, `Setup.md`) plus one `runtimes/win-x64` folder containing four Whisper DLLs. Whisper.net loads those by explicit path, so keeping that small native directory is safer than changing its loader or moving all program files into a cosmetic subfolder. Do not copy only the EXE: keep the full compact package together.

At first launch, the .NET host extracts bundled native components into its per-user cache (or `DOTNET_BUNDLE_EXTRACT_BASE_DIR` if explicitly configured). This needs writable disk space; single-file is a simpler distribution, not zero disk usage. `AppContext.BaseDirectory` continues to identify the portable EXE directory, so settings, models, history and updater staging keep their existing locations. Models remain outside the EXE and are not included in releases.

The package smoke test executes the extracted EXE with `--verify-package <report.json>` before normal application initialization. It verifies the single-file layout and portable base directory, exercises native graphics, writes/reads a disposable SQLite database, and loads Whisper without downloading a model or opening a microphone. Its report is included with CI test artifacts. Live transcription, user authentication and interactive UI remain separate acceptance tests.

Existing installations may retain old loose libraries after copying over a compact package: this change deliberately does not delete files of unknown ownership. Use a freshly extracted folder for the minimal layout; preserve/migrate your data separately. This PR does not depend on, merge, or repair the separate GitHub updater PR.
