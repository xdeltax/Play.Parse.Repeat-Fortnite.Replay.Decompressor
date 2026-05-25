# Release Workflow

This repository follows a small, repeatable release flow:

1. Keep the next release note in `CHANGELOG.md` under a dated version heading using the `-xdx` suffix.
2. Build and validate the two shipped EXEs with the GitHub Actions release workflow.
3. Tag the release as `v<version>-xdx` so `.github/workflows/release.yml` publishes the assets.
4. The release assets are the single-file Windows EXEs for `ConsoleReader` and `ReplayWatcher`, renamed to start with `YYYY.MM.DD - ` and include `xdx`.

Release style used here:

- Changelog format: Keep a Changelog.
- Versioning: Semantic Versioning with an `-xdx` release suffix for this fork's published releases.
- Release artifacts: `YYYY.MM.DD - ConsoleReader-xdx.exe` and `YYYY.MM.DD - ReplayWatcher-xdx.exe`.