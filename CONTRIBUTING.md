# Contributing to DevBrain

Thanks for your interest in contributing to DevBrain!

## Local Development Setup

1. **Prerequisites**
   - [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
   - [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
   - [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) (logged in via `az login`)
   - A Cosmos DB account (or the [Cosmos DB Emulator](https://learn.microsoft.com/azure/cosmos-db/local-emulator))

2. **Clone and build**
   ```bash
   git clone https://github.com/Ignite-Solutions-Group/devbrain.git
   cd devbrain
   dotnet build
   ```

3. **Configure local settings**
   ```bash
   cp src/DevBrain.Functions/local.settings.json.example src/DevBrain.Functions/local.settings.json
   ```
   Edit `local.settings.json` with your Cosmos DB account endpoint.

4. **Run locally**
   ```bash
   cd src/DevBrain.Functions
   func start
   ```

## Pull Request Process

1. Fork the repository and create a feature branch from `main`.
2. Make your changes. Keep commits focused and atomic.
3. Ensure `dotnet build` completes with no warnings (warnings are treated as errors).
4. Open a pull request against `main` with a clear description of the change.
5. A maintainer will review and merge once CI passes.

## Code Style

- Follow existing patterns in the codebase.
- Nullable reference types are enabled — avoid nullable warnings.
- Keep things simple. DevBrain is deliberately minimal.

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
