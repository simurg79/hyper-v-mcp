using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Runtime tests for host-scoped remoting resolution.
/// See /myplans/remoting/remoting-design.md — Host Connection Configuration.
/// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D3: All tools accept optional hostId (defaults to "local").
///
/// These tests exercise the REAL HostResolver implementation against expected
/// runtime behavior. They will fail with NotImplementedException until the
/// resolver is fully implemented.
///
/// Expected runtime flows:
/// - null/empty hostId defaults to "local"
/// - Known hostId resolves to the configured HostProfile
/// - Unknown hostId throws HostNotFoundException (or returns null for Resolve)
/// - Local host profiles are identified correctly (IsLocal = true)
///
/// HOW TO MAKE THESE PASS:
/// 1. Implement HostResolver.Resolve to look up hostId in ServerOptions.Hosts.
/// 2. Default null/empty hostId to ServerOptions.DefaultHostId.
/// 3. HostResolver.ResolveRequired must throw HostNotFoundException for unknown hostId.
/// </summary>
[Trait("Category", "Runtime")]
public class HostResolutionTests
{
    private ServerOptions CreateOptionsWithHosts()
    {
        return new ServerOptions
        {
            DefaultHostId = "local",
            Hosts = new Dictionary<string, HostProfile>
            {
                ["local"] = new HostProfile
                {
                    HostId = "local",
                    ComputerName = "localhost",
                    TrustPolicy = "local"
                },
                ["hyperv-01"] = new HostProfile
                {
                    HostId = "hyperv-01",
                    ComputerName = "hyperv-01.corp.local",
                    TrustPolicy = "strict",
                    BaseVhdxPath = @"D:\Images\base.vhdx"
                }
            }
        };
    }

    // ─── Default HostId Resolution ─────────────────────────────────────

    /// <summary>
    /// null hostId must default to the configured DefaultHostId ("local").
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D3.
    /// </summary>
    [Fact]
    public void Null_HostId_Resolves_To_Default_Local()
    {
        var resolver = new HostResolver(CreateOptionsWithHosts());

        var profile = resolver.Resolve(null);

        profile.Should().NotBeNull("null hostId should default to 'local'");
        profile!.HostId.Should().Be("local",
            "null hostId must resolve to DefaultHostId " +
            "(see /myplans/mcp-interface/mcp-interface-design.md — MCP-D3)");
        profile.IsLocal.Should().BeTrue();
    }

    /// <summary>
    /// Empty string hostId must default to "local".
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D3.
    /// </summary>
    [Fact]
    public void Empty_HostId_Resolves_To_Default_Local()
    {
        var resolver = new HostResolver(CreateOptionsWithHosts());

        var profile = resolver.Resolve("");

        profile.Should().NotBeNull("empty hostId should default to 'local'");
        profile!.HostId.Should().Be("local");
    }

    // ─── Known Host Resolution ─────────────────────────────────────────

    /// <summary>
    /// Known hostId must resolve to the configured HostProfile.
    /// See /myplans/remoting/remoting-design.md — Host Connection Configuration.
    /// </summary>
    [Fact]
    public void Known_HostId_Resolves_To_Profile()
    {
        var resolver = new HostResolver(CreateOptionsWithHosts());

        var profile = resolver.Resolve("hyperv-01");

        profile.Should().NotBeNull("known hostId should resolve to a profile");
        profile!.HostId.Should().Be("hyperv-01");
        profile.ComputerName.Should().Be("hyperv-01.corp.local");
        profile.TrustPolicy.Should().Be("strict",
            "remote hosts should use 'strict' trust policy " +
            "(see /myplans/security/trust-certificates/trust-certificates-design.md — ADR-7)");
        profile.IsLocal.Should().BeFalse("remote host should not be local");
    }

    /// <summary>
    /// Local host profile must be correctly identified via IsLocal.
    /// See /myplans/remoting/remoting-design.md — Host Connection Configuration: isLocal is computed.
    /// </summary>
    [Fact]
    public void Local_HostId_Resolves_As_Local()
    {
        var resolver = new HostResolver(CreateOptionsWithHosts());

        var profile = resolver.Resolve("local");

        profile.Should().NotBeNull();
        profile!.IsLocal.Should().BeTrue(
            "computerName 'localhost' should resolve as local");
    }

    // ─── Unknown Host Resolution ───────────────────────────────────────

    /// <summary>
    /// Unknown hostId must return null from Resolve (non-throwing variant).
    /// </summary>
    [Fact]
    public void Unknown_HostId_Returns_Null()
    {
        var resolver = new HostResolver(CreateOptionsWithHosts());

        var profile = resolver.Resolve("nonexistent-host");

        profile.Should().BeNull(
            "unknown hostId must return null from Resolve");
    }

    /// <summary>
    /// Unknown hostId must throw HostNotFoundException from ResolveRequired.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: HOST_NOT_FOUND.
    /// </summary>
    [Fact]
    public void Unknown_HostId_Throws_HostNotFoundException()
    {
        var resolver = new HostResolver(CreateOptionsWithHosts());

        var act = () => resolver.ResolveRequired("nonexistent-host");

        act.Should().Throw<HostNotFoundException>(
            "unknown hostId must throw HostNotFoundException " +
            "(see /myplans/mcp-interface/mcp-interface-design.md — HOST_NOT_FOUND)")
            .Where(ex => ex.HostId == "nonexistent-host");
    }

    // ─── Host Profile Property Forwarding ──────────────────────────────

    /// <summary>
    /// Resolved profile must carry through optional configuration overrides.
    /// See /myplans/design.md §8 — Host Configuration File.
    /// </summary>
    [Fact]
    public void Resolved_Profile_Carries_Optional_Overrides()
    {
        var resolver = new HostResolver(CreateOptionsWithHosts());

        var profile = resolver.Resolve("hyperv-01");

        profile.Should().NotBeNull();
        profile!.BaseVhdxPath.Should().Be(@"D:\Images\base.vhdx",
            "optional overrides should be carried through on resolution");
    }
}
