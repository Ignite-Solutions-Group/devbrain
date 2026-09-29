# Contributing to DevBrain

Thanks for your interest in contributing to DevBrain!

## Local Development Setup

1. **Prerequisites**
   - [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
   - [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) (logged in via `az login`)
   - A dev DevBrain environment to point at (Cosmos DB, storage, and Key Vault). The server authenticates to them with `DefaultAzureCredential`, so the key-based Cosmos DB Emulator isn't supported.

2. **Clone and build**
   ```bash
   git clone https://github.com/Ignite-Solutions-Group/devbrain.git
   cd devbrain
   dotnet build
   ```

3. **Configure local settings**

   Configure the required settings with .NET user secrets or environment variables. The server fails fast when any required value is missing. [Local Development](README.md#local-development) in the README lists every setting and the roles your `az login` identity needs.

4. **Run locally**
   ```bash
   dotnet run --project src/DevBrain.Server
   ```

   The MCP endpoint is `/mcp` and the anonymous health endpoint is `/healthz`.

## Pull Request Process

1. Fork the repository and create a feature branch from `main`.
2. Make your changes. Keep commits focused and atomic.
3. Ensure `dotnet build` completes with no warnings (warnings are treated as errors) and `dotnet test --solution devbrain.slnx` passes. Tests run on xUnit v3 through Microsoft.Testing.Platform, which `global.json` opts into.
4. For dependency changes, check the whole solution from the repository root before opening the PR:
   ```bash
   dotnet list devbrain.slnx package --vulnerable --include-transitive
   dotnet list devbrain.slnx package --outdated
   dotnet list devbrain.slnx package --deprecated --include-transitive
   ```
5. For a release, bump `<Version>` in `Directory.Build.props`. It's the single source for assembly versions and the MCP `serverInfo` version. Then add the matching `CHANGELOG.md` entry.
6. Open a pull request against `main` with a clear description of the change.
7. A maintainer will review and merge once CI passes.

## Code Style

- Follow existing patterns in the codebase.
- Nullable reference types are enabled — avoid nullable warnings.
- Keep things simple. DevBrain is deliberately minimal.

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
