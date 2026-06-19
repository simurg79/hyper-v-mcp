using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Models;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Remoting;

/// <summary>
/// Tests for remoting contract validation: hostId targeting, host profiles,
/// and the two-tier connection model.
///
/// See /myplans/remoting/remoting-design.md — Two-Tier Connection Architecture.
/// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D3: All tools accept optional hostId.
///
/// HOW TO MAKE THESE PASS:
/// 1. Implement HostProfile validation in ServerOptions or a dedicated validator.
/// 2. Implement hostId resolution logic that defaults to "local".
/// 3. Wire up host connection configuration to the session store.
/// </summary>
public class RemotingContractTests
{
    // ─── Host Profile Validation ───────────────────────────────────────

    /// <summary>
    /// A local host profile must be correctly identified via IsLocal computed property.
    /// See /myplans/remoting/remoting-design.md — Host Connection Configuration: isLocal is computed.
    /// </summary>
    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData(".")]
    public void HostProfile_IsLocal_True_For_LocalHostNames(string computerName)
    {
        var profile = new HostProfile
        {
            HostId = "test",
            ComputerName = computerName
        };

        profile.IsLocal.Should().BeTrue(
            $"computerName '{computerName}' should resolve as local " +
            "(see /myplans/remoting/remoting-design.md — Host Connection Configuration)");
    }

    /// <summary>
    /// Remote hostnames must not be identified as local.
    /// </summary>
    [Theory]
    [InlineData("hyperv-01.corp.local")]
    [InlineData("192.168.1.100")]
    [InlineData("remote-server")]
    public void HostProfile_IsLocal_False_For_RemoteHostNames(string computerName)
    {
        var profile = new HostProfile
        {
            HostId = "test",
            ComputerName = computerName
        };

        profile.IsLocal.Should().BeFalse(
            $"computerName '{computerName}' should not resolve as local");
    }

    /// <summary>
    /// Default trust policy must be "local".
    /// See /myplans/security/trust-certificates/trust-certificates-design.md — ADR-7.
    /// </summary>
    [Fact]
    public void HostProfile_Default_TrustPolicy_Is_Local()
    {
        var profile = new HostProfile
        {
            HostId = "test",
            ComputerName = "localhost"
        };

        profile.TrustPolicy.Should().Be("local",
            "default trust policy should be 'local' " +
            "(see /myplans/security/trust-certificates/trust-certificates-design.md — ADR-7)");
    }

    /// <summary>
    /// WinRM to remote hosts must default to HTTPS (UseSsl = true).
    /// See /myplans/security/security-design.md — SEC-D6: WinRM always HTTPS.
    /// </summary>
    [Fact]
    public void HostProfile_Default_UseSsl_Is_True()
    {
        var profile = new HostProfile
        {
            HostId = "test",
            ComputerName = "remote-server"
        };

        profile.UseSsl.Should().BeTrue(
            "WinRM connections must always use HTTPS " +
            "(see /myplans/security/security-design.md — SEC-D6)");
    }

    // ─── Host ID Defaulting ────────────────────────────────────────────

    /// <summary>
    /// The default hostId must be "local" per MCP-D3.
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D3.
    /// </summary>
    [Fact]
    public void ServerOptions_Default_HostId_Is_Local()
    {
        var options = new ServerOptions();

        options.DefaultHostId.Should().Be("local",
            "default hostId must be 'local' when not specified " +
            "(see /myplans/mcp-interface/mcp-interface-design.md — MCP-D3)");
    }

    // ─── Host-Scoped Tool Contract ─────────────────────────────────────

    /// <summary>
    /// All tools except vm_echo must be host-scoped (accept hostId).
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D3.
    /// </summary>
    [Theory]
    [InlineData("vm_create")]
    [InlineData("vm_start")]
    [InlineData("vm_stop")]
    [InlineData("vm_restart")]
    [InlineData("vm_destroy")]
    [InlineData("vm_list")]
    [InlineData("vm_status")]
    [InlineData("vm_wait_ready")]
    [InlineData("vm_run_command")]
    [InlineData("vm_run_script")]
    [InlineData("vm_copy_file")]
    [InlineData("vm_get_file")]
    [InlineData("vm_checkpoint")]
    [InlineData("vm_list_images")]
    [InlineData("vm_cleanup_orphans")]
    [InlineData("vm_configure")]
    [InlineData("vm_pause")]
    [InlineData("vm_resume")]
    public void HostScoped_Tool_Exists_In_Catalog(string toolName)
    {
        ToolCatalog.HostScopedTools
            .Should().Contain(t => t.Name == toolName,
                $"tool '{toolName}' must be host-scoped (accept hostId parameter) " +
                "(see /myplans/mcp-interface/mcp-interface-design.md — MCP-D3)");
    }

    /// <summary>
    /// When a tool is called with an unknown hostId, the server must return
    /// HOST_NOT_FOUND error. This test validates the error code contract.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: HOST_NOT_FOUND.
    /// </summary>
    [Fact]
    public void Unknown_HostId_Produces_HostNotFound_Error()
    {
        // Arrange: simulate an error response for unknown hostId
        var response = McpToolResponse.Fail(
            "No host with the specified hostId 'nonexistent-host' is configured",
            ErrorCodes.HostNotFound);

        // Assert: validate the error envelope shape
        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("HOST_NOT_FOUND");
        response.Error.Should().Contain("nonexistent-host");
        response.Data.Should().BeNull(
            "failed responses must have null data " +
            "(see /myplans/mcp-interface/mcp-interface-design.md — Failure Response)");
    }

    /// <summary>
    /// When a remote host is unreachable, the server must return HOST_UNREACHABLE error.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: HOST_UNREACHABLE.
    /// </summary>
    [Fact]
    public void Unreachable_Host_Produces_HostUnreachable_Error()
    {
        var response = McpToolResponse.Fail(
            "Cannot connect to remote host 'hyperv-01.corp.local'",
            ErrorCodes.HostUnreachable);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("HOST_UNREACHABLE");
    }

    /// <summary>
    /// SessionFailed error code must be available for guest session failures.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: SESSION_FAILED.
    /// </summary>
    [Fact]
    public void Guest_Session_Failure_Produces_SessionFailed_Error()
    {
        var response = McpToolResponse.Fail(
            "Could not establish guest session to VM 'test-vm' on host 'local'",
            ErrorCodes.SessionFailed);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("SESSION_FAILED");
    }

    // ─── Host Profile Configuration ────────────────────────────────────

    /// <summary>
    /// ServerOptions must support multi-host configuration.
    /// See /myplans/design.md §8 — Host Configuration File.
    /// </summary>
    [Fact]
    public void ServerOptions_Supports_Multiple_Host_Profiles()
    {
        var options = new ServerOptions
        {
            Hosts = new Dictionary<string, HostProfile>
            {
                ["local"] = new HostProfile
                {
                    HostId = "local",
                    ComputerName = "localhost",
                    TrustPolicy = "local"
                },
                ["hyperv-server-01"] = new HostProfile
                {
                    HostId = "hyperv-server-01",
                    ComputerName = "hyperv-01.corp.local",
                    TrustPolicy = "strict",
                    BaseVhdxPath = @"D:\Images\windows-11-clean.vhdx",
                    StorageRoot = @"D:\VMs\"
                }
            }
        };

        options.Hosts.Should().HaveCount(2);
        options.Hosts["local"].IsLocal.Should().BeTrue();
        options.Hosts["hyperv-server-01"].IsLocal.Should().BeFalse();
        options.Hosts["hyperv-server-01"].TrustPolicy.Should().Be("strict",
            "remote hosts should use 'strict' trust policy " +
            "(see /myplans/security/trust-certificates/trust-certificates-design.md — ADR-7)");
    }

    /// <summary>
    /// Host profiles with optional overrides must preserve base defaults.
    /// </summary>
    [Fact]
    public void HostProfile_Optional_Overrides_Are_Nullable()
    {
        var profile = new HostProfile
        {
            HostId = "local",
            ComputerName = "localhost"
        };

        profile.BaseVhdxPath.Should().BeNull("optional override should default to null");
        profile.StorageRoot.Should().BeNull("optional override should default to null");
    }
}
