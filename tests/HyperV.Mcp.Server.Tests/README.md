# Hyper-V MCP Server — Test Suite

## Overview

This test suite defines the **expected MCP behavior contract** for the Hyper-V MCP Server using a **test-first** approach. Tests are written _before_ implementation to codify the design decisions captured in the project's internal design notes.

## Test Structure

```
tests/HyperV.Mcp.Server.Tests/
├── McpInterface/
│   ├── ToolDiscoveryTests.cs       — Tool catalog completeness (19 tools, 9 categories)
│   └── ErrorEnvelopeTests.cs       — Response envelope shape, error code taxonomy, JSON wire format
├── Remoting/
│   └── RemotingContractTests.cs    — Host profile validation, hostId targeting, multi-host config
├── Operational/
│   └── ConcurrencyTests.cs         — Concurrency limits, backpressure response, lock hierarchy
├── Runtime/
│   ├── ToolDispatchTests.cs        — Tool dispatch/registration runtime behavior
│   ├── HostResolutionTests.cs      — hostId resolution and host profile lookup
│   ├── ConcurrencyGateRuntimeTests.cs — Acquire/release semantics, CONCURRENCY_LIMIT translation
│   ├── ErrorMappingRuntimeTests.cs — Exception-to-MCP-response mapping
│   ├── TimeoutBehaviorTests.cs     — Timeout with partial output, session preservation
│   ├── VmLifecycleFlowTests.cs     — VM create/start/stop/destroy orchestration
│   └── CheckpointFileTransferRemotingFlowTests.cs — Checkpoint/file-transfer/remoting flows
└── README.md                       — This file
```

## Running Tests

```bash
# Run all tests and see the authoritative count
dotnet test --verbosity normal
```

> **Note:** Test counts below are intentionally omitted to avoid documentation drift.
> Run `dotnet test` to get the current authoritative count. All tests should pass (GREEN).

## Expected Test Status

All tests are **GREEN**. Both contract/model tests and runtime tests pass.

### Contract Tests

Contract/model tests validate shapes, constants, and configuration defaults:

| Test File | Scope | Status |
|-----------|-------|--------|
| `McpInterface/ToolDiscoveryTests.cs` | Tool catalog completeness, categories, priorities, immutability | GREEN |
| `McpInterface/ErrorEnvelopeTests.cs` | Response envelope shape, error code taxonomy, JSON wire format | GREEN |
| `Remoting/RemotingContractTests.cs` | Host profile validation, hostId targeting, multi-host config | GREEN |
| `Operational/ConcurrencyTests.cs` | Concurrency limits, backpressure, lock hierarchy, defaults | GREEN |

### Runtime Tests

Runtime tests exercise real implementations of `ToolDispatcher`, `HostResolver`, `ConcurrencyGate`, and `ErrorMapper`. Tests using **Moq for orchestration** mock infrastructure dependencies to define expected wiring patterns.

| Test File | Scope | Status | Implementation |
|-----------|-------|--------|----------------|
| `ToolDispatchTests.cs` | Tool dispatch/registration behavior | GREEN | `ToolDispatcher` |
| `HostResolutionTests.cs` | hostId default and profile lookup | GREEN | `HostResolver` |
| `ConcurrencyGateRuntimeTests.cs` | Acquire/release, limits, cancellation | GREEN | `ConcurrencyGate` |
| `ErrorMappingRuntimeTests.cs` | Exception-to-response mapping | GREEN | `ErrorMapper` |
| `TimeoutBehaviorTests.cs` | Timeout partial output, session preservation | GREEN | Mocked (behavioral contract) |
| `VmLifecycleFlowTests.cs` | Lifecycle orchestration flows | GREEN | Mocked (orchestration flow) |
| `CheckpointFileTransferRemotingFlowTests.cs` | Checkpoint/file-transfer/remoting flows | GREEN | Mocked (orchestration flow) |

## Key Design References

Each test file's doc comments cite the specific internal design-doc sections (MCP-D*, CC-D*, CMD-D*, and so on) that the test pins. Those design documents are maintained in a separate internal vault and are not part of this public repository; the test source itself is the authoritative behavior contract.

## Conventions

- All test classes include doc comments explaining _how to make tests pass_
- Design doc links are included in assertion failure messages
- Tests use `FluentAssertions` for readable failure output
- Runtime tests use `Moq` for mocking infrastructure dependencies
- Tests are categorized with `[Trait("Category", "Runtime")]` for runtime tests
- No real Hyper-V integration — all tests are deterministic unit tests

## Live Test VM: win11-mcp-test

A persistent Windows 11 25H2 VM created via `vm_os_install` for live
end-to-end testing of MCP tools that require a running Windows guest
(`vm_run_command`, `vm_run_script`, `vm_copy_file`, `vm_get_file`,
`vm_checkpoint`, `vm_status`, `vm_wait_ready`, lifecycle ops).

> **Single source of truth.** Credentials, env-var contract, specs, switch,
> and recreation instructions for `win11-mcp-test` were relocated to the
> operator-local roo-vault at `myplans/operational/lab-environment.md`
> (not tracked in this repo). Do **not** duplicate the credential or env-var
> values into this README; refer there instead. The companion preflight was
> relocated to `myscripts/smoke-test/_phase1-preflight.ps1` (also not tracked
> in this repo), and the contract test that pins the env-var names is
> [`Runtime/CredentialResolverContractTests.cs`](Runtime/CredentialResolverContractTests.cs).
