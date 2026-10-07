# Hyper-V MCP Server — Test Suite

## Overview

This test suite defines the **expected MCP behavior contract** for the Hyper-V MCP Server using a **test-first** approach. Tests codify tool contracts, configuration defaults, and runtime behavior.

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

`dotnet test` also runs the Python suite for the Ubuntu autoinstall ISO
preparation helper: [`Runtime/Issue358PreparationHelperPythonSuiteTests.cs`](Runtime/Issue358PreparationHelperPythonSuiteTests.cs)
shells out to `scripts/test_prepare_ubuntu_autoinstall_iso.py`. It can also be
run directly:

```bash
python scripts/test_prepare_ubuntu_autoinstall_iso.py
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

## Conventions

- All test classes include doc comments explaining _how to make tests pass_
- Assertion failure messages explain the expected behavior
- Tests use `FluentAssertions` for readable failure output
- Runtime tests use `Moq` for mocking infrastructure dependencies
- Tests are categorized with `[Trait("Category", "Runtime")]` for runtime tests
- No real Hyper-V integration — all tests are deterministic unit tests

## Live test environment

Configure a dedicated test VM for live end-to-end testing of MCP tools that require a running Windows guest
(`vm_run_command`, `vm_run_script`, `vm_copy_file`, `vm_get_file`,
`vm_checkpoint`, `vm_status`, `vm_wait_ready`, lifecycle ops).

Keep credentials, VM identities, switch configuration, storage layout, and
recreation instructions in private operator configuration, not in this
repository. The credential environment-variable contract is tested in
[`Runtime/CredentialResolverContractTests.cs`](Runtime/CredentialResolverContractTests.cs).
