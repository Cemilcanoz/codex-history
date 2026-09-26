# Contributing

Small, reproducible improvements are welcome. Use synthetic logs for all development, tests and screenshots. Do not share session contents, authentication files or your local index in issues or pull requests.

## Local checks

On Windows with the .NET 10 SDK specified in `global.json`:

```powershell
dotnet build CodexHistory.sln --configuration Release
dotnet test CodexHistory.sln --configuration Release --no-build
./scripts/Run-Demo.ps1
```

The smoke script creates synthetic data and checks selection, filters, timeline, quotas, content preview, themes and compact window behavior. It writes screenshots to the ignored `artifacts/` folder and exits automatically.

Keep changes focused. For parser or indexing changes, add a synthetic regression that fails before the fix. For UI changes, review the light, dark and compact screenshots. Explain the trigger, expected result and validation in your pull request.

## Reporting issues

Include Windows version, app version, the steps to reproduce and expected behavior. Reduce log-format bugs to a synthetic JSONL example with fabricated workspace paths and contents. Feature requests should describe the task they help someone complete.

## Structure

- `Core`: models and domain rules.
- `Infrastructure`: parser, index and SQLite queries.
- `App`: WPF interface and opt-in content preview.
- `Cli`: explicit source-folder indexing.
- `tests`: synthetic regression cases.

See [architecture decisions](docs/decisions.md) for the rationale. Contribution terms follow the repository's [MIT license](LICENSE).
