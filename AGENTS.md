# Repository instructions

## Release policy

Every version of UsageAI must complete all of the following steps:

1. Add a dated entry for the version to `Changelog.md` before release.
2. Keep the version in `UsageAI.csproj`, the changelog heading, and the Git tag identical.
3. Use Semantic Versioning and create an annotated tag named `vX.Y.Z`.
4. Push both the release commit and its tag to `origin`.
5. Publish a non-draft GitHub release from that exact tag, using the matching changelog entry as its release notes.
6. Attach the Windows release ZIP to the GitHub release and verify that the asset uploaded successfully.

A version is not complete until its changelog entry, tag, and published release all exist and agree.

## Provider integrations

When adding a new provider, use [nesszer/Win-CodexBar](https://github.com/nesszer/Win-CodexBar) as the implementation reference for the provider's authentication method and any other relevant provider-specific data, including credential sources, API endpoints, request requirements, usage and limit fields, reset semantics, and error handling. Adapt the implementation to UsageAI's architecture and never log, expose, or commit provider secrets.
