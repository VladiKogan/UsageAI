# Repository instructions

## Release process

UsageAI does not use automated release publishing. Package and publish releases
manually.

The `Build` workflow validates pushes and pull requests by restoring, building,
and running the test project. It never publishes, tags, uploads artifacts, or
writes to the repository; it is a check, not a release pipeline.

Before a release, run the local build and tests, add a dated changelog entry,
and keep the version in `UsageAI.csproj`, changelog heading, and annotated
`vX.Y.Z` Git tag identical. Push the release commit and tag, then create the
GitHub release and upload its Windows ZIP manually.

## Local validation

```powershell
dotnet build .\UsageAI.sln -c Release
dotnet run --project .\UsageAI.Tests\UsageAI.Tests.csproj
```

The test project is a plain console harness rather than a test-runner package,
so it runs with `dotnet run`, not `dotnet test`. Set `USAGEAI_DATA_DIR` to keep
local state out of `%LOCALAPPDATA%` while experimenting.

## Provider integrations

When adding a new provider, use [nesszer/Win-CodexBar](https://github.com/nesszer/Win-CodexBar) as the implementation reference for the provider's authentication method and any other relevant provider-specific data, including credential sources, API endpoints, request requirements, usage and limit fields, reset semantics, and error handling. Adapt the implementation to UsageAI's architecture and never log, expose, or commit provider secrets.
