# Windows CI and releases

The `Windows build, test and package` check builds with the SDK pinned in global.json, runs the unit suite, publishes a self-contained Windows x64 folder and validates every file after ZIP extraction. All jobs use Windows runners. Three signed-in live AI tests are intentionally skipped without an authorized profile; CI is not a claim that live microphone/AI integrations were tested.

PRs validate without repository write permissions. A push to main repeats validation for that exact source SHA and only then publishes `trayvora-win-x64.zip`, `SHA256SUMS.txt` and `release.json`. No token/PAT secret is needed: only the release job has contents:write. Tag creation does not trigger this workflow. No version commit is pushed, so there is no workflow loop.

Version is `0.1.(first-parent commit count - 1)`, starting with 0.1.1 for the first integration after the initial commit. Main history must remain linear/append-only, with the existing no-force-push protection. Squash/merge each advances one step; a rebase integration may advance multiple patch numbers. Gaps are valid. Each main push has independent jobs (no cancellation queue), so closely spaced merges are not dropped. Published releases and tags are never overwritten; inconsistent retries fail closed. Existing draft assets are downloaded and compared before publication. A late older run is not marked latest.

Manual workflow dispatch runs validation only, never publication. To retry a failed release, rerun the original main push workflow. A build/package rerun that produces different bytes from an already-created draft fails safely and requires investigation instead of silently replacing files.

GitHub rules: default branch requires PR and the Windows check, blocks deletion/force push; release v* tags block update/deletion. No bypass actors. Actions token defaults read-only and cannot approve PRs. Package/test artifacts expire after 7 days; published release assets remain.

Local verification (fresh output folder required):

```powershell
./scripts/ci/build.ps1 -OutputRoot 'E:\Temp\trayvora-ci-check-unique'
```

This performs build/tests/package validation without publishing. No existing output directory is deleted. Native dependencies and the .NET runtime are included; speech models and local user data are not.
