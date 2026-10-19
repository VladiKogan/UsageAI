# Contributing to UsageAI

Thanks for helping. UsageAI has two parts that ship separately:

- the **Windows tray app** (C#, WinForms, .NET 10) at the repository root
- the **VS Code / Antigravity extension** (TypeScript) in `extension\`

## Before you start

Open an issue, or comment on an existing one, before starting anything bigger than a small fix, so
we can agree on the approach and avoid two people building the same thing.

## Set up and check your change

Desktop app (needs the .NET SDK pinned in `global.json`):

```powershell
dotnet build .\UsageAI.sln -c Release
dotnet run --project .\UsageAI.Tests\UsageAI.Tests.csproj
.\scripts\verify-coverage.ps1
```

The tests are a plain console harness, so they run with `dotnet run`, not `dotnet test`. A new check
must be added to the `Tests` array at the top of `UsageAI.Tests\Program.cs`. Run a single check with
`-- --filter "part of check name"`. `--diagnose <codex|claude|copilot|gemini>` prints one provider as
JSON, which is handy for debugging.

Extension (needs Node 22 or newer):

```powershell
cd .\extension
npm.cmd ci
npm.cmd run lint
npm.cmd test
npm.cmd run test:coverage
```

CI runs the same build, tests and coverage gates on every pull request: 83% line and 76% branch for
the desktop app, and 70% line, 65% branch and 75% function for the extension.

## Ground rules

- **No new dependencies**, in the app, the extension runtime, or the test harnesses. If you think one
  is needed, raise it in the issue first.
- **Warnings are errors.** Both projects build with analyzers and `TreatWarningsAsErrors`.
- **Never expose a secret.** Errors, diagnostics and logs may include an account name or usage
  numbers, but never a token, cookie or refresh credential. Don't commit real credentials, even in
  tests.
- **Use the existing safe helpers** instead of raw HTTP, file or process calls:
  `Services\SecurityUtilities.cs` on the desktop and `extension\src\security.ts` in the extension.
  They handle redirects, size limits, minimal child environments and bounded output.
- **Keep the desktop app and the extension in step** when you change provider behaviour that both
  of them implement.
- [SECURITY.md](SECURITY.md) describes how credentials and child processes are handled. Changes in
  that area should keep to it.

## Adding or changing a provider

- Each provider lives in one client (`Services\*UsageClient.cs`, `extension\src\providers\*.ts`).
- Provider error messages are shown to users as written, so keep them clear and free of internals.
  A new desktop provider's exception type must be added to `Program.GetSafeDiagnosticError` and
  `UsageRefreshService.FetchAsync`.
- [nesszer/Win-CodexBar](https://github.com/nesszer/Win-CodexBar) is a useful reference for provider
  endpoints, credential locations and limit semantics.

## Changelog, commits and pull requests

- Add an entry under `## Unreleased` in `Changelog.md` (desktop) or `extension\CHANGELOG.md`
  (extension). Start each bullet with a sentence that stands on its own, then explain. The app's
  What's New window shows only that first sentence.
- Commit messages follow `type(scope): summary`, for example `fix(extension): ...` or
  `feat(gemini): ...`, and say *why* in the body.
- Keep pull requests focused on one change and reference the issue (`Closes #123`).
- Don't bump versions or create releases; the maintainer does that.

## Security issues

Please don't open a public issue for a security problem. Use the repository's **Security** tab to
send a private report, as described in [SECURITY.md](SECURITY.md).

By contributing, you agree that your contribution is licensed under the [MIT License](LICENSE).
