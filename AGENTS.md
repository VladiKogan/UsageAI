# Repository instructions

## Release process

UsageAI does not use hosted CI/CD or automated release publishing. Validate,
package, and publish releases manually.

Before a release, run the local build and tests, add a dated changelog entry,
and keep the version in `UsageAI.csproj`, changelog heading, and annotated
`vX.Y.Z` Git tag identical. Push the release commit and tag, then create the
GitHub release and upload its Windows ZIP manually.

## Provider integrations

When adding a new provider, use [nesszer/Win-CodexBar](https://github.com/nesszer/Win-CodexBar) as the implementation reference for the provider's authentication method and any other relevant provider-specific data, including credential sources, API endpoints, request requirements, usage and limit fields, reset semantics, and error handling. Adapt the implementation to UsageAI's architecture and never log, expose, or commit provider secrets.
