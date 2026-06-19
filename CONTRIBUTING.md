# Contributing to Hyper-V MCP Server

Thanks for your interest in contributing! This document covers how to build,
test, and submit changes.

## Prerequisites

| Requirement | Detail |
|-------------|--------|
| Host OS | Windows 10/11 Pro, Enterprise, or Server with the Hyper-V role enabled |
| .NET SDK | .NET 8.0 or later |
| PowerShell | PowerShell 7+ (`pwsh.exe`) preferred; falls back to Windows PowerShell 5.1 |
| Privilege | Running the server (and live tests) requires an elevated (admin) process |

You can build and run the unit-test suite without Hyper-V. Only the
`RequiresHyperV` and `LiveE2E` test categories need a real Hyper-V host.

## Build

```pwsh
dotnet restore HyperV.Mcp.Server.sln
dotnet build HyperV.Mcp.Server.sln --configuration Release
```

## Run the tests

Most tests are deterministic unit tests with no Hyper-V dependency. Run the
host-independent suite with:

```pwsh
dotnet test --filter "Category!=RequiresHyperV&Category!=LiveE2E"
```

The Hyper-V-dependent suites run on a self-hosted Windows runner with the
Hyper-V role enabled (see [`.github/workflows/ci-hyperv.yml`](.github/workflows/ci-hyperv.yml)):

```pwsh
# Requires an elevated shell on a Hyper-V host
dotnet test --filter "Category=RequiresHyperV"
dotnet test --filter "Category=RequiresHyperV|Category=LiveE2E"   # includes slow live E2E
```

## Branch naming

Use a short, kind-prefixed branch name:

- `feat/<short-description>` — new functionality
- `fix/<short-description>` — bug fixes
- `docs/<short-description>` — documentation only
- `chore/<short-description>` — tooling, build, or housekeeping

## Pull requests

1. Keep PRs focused — one logical change per PR.
2. Add or update tests for any behavior change. New tools and error paths
   should be covered by the contract/runtime test suites under `tests/`.
3. Make sure `dotnet build` and the host-independent test filter both pass
   locally before opening the PR.
4. Describe the change, link any related issue, and note whether you ran the
   Hyper-V-dependent tests.

## Coding conventions

- Target framework is `net8.0-windows`; keep the server console-only.
- Each tool handler carries inline contract notes — keep those accurate when
  you change behavior.
- Prefer small, well-named methods; match the surrounding style.

## Reporting bugs and requesting features

Please use the issue templates under
[`.github/ISSUE_TEMPLATE/`](.github/ISSUE_TEMPLATE). For anything
security-related, follow [SECURITY.md](SECURITY.md) instead of opening a
public issue.

## License

By contributing, you agree that your contributions will be licensed under the
[Apache License, Version 2.0](LICENSE).
