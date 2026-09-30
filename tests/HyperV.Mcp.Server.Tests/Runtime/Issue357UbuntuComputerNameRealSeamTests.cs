using System.Diagnostics;
using System.Text.RegularExpressions;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #357 — <c>vm_os_install</c> with an Ubuntu ISO died at <c>vm-create</c> with
/// "New-VM : Value cannot be null. Parameter name: name" because the emitted script omitted
/// <c>-ComputerName localhost</c>: the Hyper-V module then resolves the host from the
/// <c>COMPUTERNAME</c> environment variable, which is absent from the child powershell.exe
/// environment block, and PS 5.1 throws. The <c>-ErrorAction SilentlyContinue</c> call sites
/// (notably <c>Enable-VMIntegrationService</c> for KVP) failed the same way but silently.
///
/// The existing suite recognized these scripts by substring and never asserted the parameter, so
/// the fix could regress while staying green. These guards close that hole two ways:
///   1. the PRODUCTION <see cref="UbuntuAutoinstallOrchestrator"/> emits the scripts, which are then
///      executed by REAL powershell.exe over Hyper-V-shaped shims, so PowerShell's own parameter
///      binder — not a string match — reports what <c>-ComputerName</c> each cmdlet actually
///      resolved to;
///   2. a class-wide script-shape scan requires the literal <c>-ComputerName localhost</c> on every
///      name-based Hyper-V invocation, so a cmdlet added later without it fails even though no shim
///      exists for it.
///
/// See myplans/vm-management/lifecycle/lifecycle-design.md — LF-D7.
/// </summary>
[Trait("Category", "Runtime")]
[Trait("Category", "RealPowerShell")]
public class Issue357UbuntuComputerNameRealSeamTests
{
    private const string HostId = "local";
    private const string VmName = "issue357-ubuntu-vm";
    private const string IsoPath = @"C:\ISOs\ubuntu-24.04-live-server-amd64.iso";
    private const string ExpectedComputerName = "localhost";

    private readonly ITestOutputHelper _output;

    public Issue357UbuntuComputerNameRealSeamTests(ITestOutputHelper output) => _output = output;

    // ════════════════════════════════════════════════════════════════════
    // Guard 1 — real PowerShell parameter binding over the emitted scripts.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EmittedScripts_RealPowerShellBinding_EveryHyperVCmdletResolvesComputerNameLocalhost()
    {
        var stopwatch = Stopwatch.StartNew();
        using var tempScope = new TempScope();
        using var harness = new HyperVShimHarness();
        var executor = new HarnessBackedPowerShellExecutor(harness);

        await RunSuccessInstallAsync(executor, tempScope.Path);
        await RunProvisioningFailureInstallAsync(executor, tempScope.Path);

        stopwatch.Stop();
        _output.WriteLine($"observed invocations: {harness.Invocations.Count}; elapsed {stopwatch.ElapsedMilliseconds} ms");
        foreach (var invocation in harness.Invocations)
        {
            _output.WriteLine(
                $"  {invocation.Cmdlet} nameBound={invocation.NameBound} -ComputerName '{invocation.ComputerName}'");
        }

        var nameBound = harness.Invocations.FindAll(invocation => invocation.NameBound);

        nameBound.Should().HaveCountGreaterThan(10,
            "real powershell.exe must actually have executed the orchestrator's scripts and bound " +
            "its many name-based cmdlets; a small or empty recording means the harness no-opped " +
            "and the guard proves nothing");

        nameBound.Should().OnlyContain(
            invocation => invocation.ComputerName == ExpectedComputerName,
            "every name-based host-side Hyper-V cmdlet must bind -ComputerName localhost (LF-D7, issue #357)");
    }

    [Fact]
    public async Task EmittedScripts_SilentlyContinueCallSites_AreCoveredByTheBindingGuard()
    {
        using var tempScope = new TempScope();
        using var harness = new HyperVShimHarness();
        var executor = new HarnessBackedPowerShellExecutor(harness);

        await RunSuccessInstallAsync(executor, tempScope.Path);

        var observed = harness.Invocations.ConvertAll(invocation => invocation.Cmdlet);

        // These three were the silent failures: suppressed errors left KVP disabled, which is the
        // sole Ubuntu completion authority. Naming them keeps the guard above from going vacuous if
        // a refactor drops the call sites.
        observed.Should().Contain("Enable-VMIntegrationService");
        observed.Should().Contain("Get-VMDvdDrive");
        observed.Should().Contain("Get-VMNetworkAdapter");

        harness.Invocations
            .FindAll(invocation => invocation.Cmdlet == "Enable-VMIntegrationService")
            .Should().OnlyContain(invocation =>
                invocation.NameBound && invocation.ComputerName == ExpectedComputerName);
    }

    // ════════════════════════════════════════════════════════════════════
    // Guard 2 — class-wide script shape; catches a future cmdlet with no shim.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EmittedScripts_EveryNameBasedHyperVInvocation_CarriesComputerNameLocalhostLiterally()
    {
        using var tempScope = new TempScope();
        using var harness = new HyperVShimHarness();
        var executor = new HarnessBackedPowerShellExecutor(harness);

        await RunSuccessInstallAsync(executor, tempScope.Path);
        await RunProvisioningFailureInstallAsync(executor, tempScope.Path);

        var offenders = new List<string>();
        var inspected = 0;
        foreach (var script in executor.Scripts)
        {
            foreach (var line in script.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var cmdletMatch = HyperVCmdletRegex.Match(trimmed);
                if (!cmdletMatch.Success)
                {
                    continue;
                }

                // Object-bound / pipelined call sites never do name-based host resolution, so they
                // are correctly outside the invariant.
                if (!NameBoundRegex.IsMatch(trimmed))
                {
                    continue;
                }

                inspected++;
                if (!ComputerNameLocalhostRegex.IsMatch(trimmed))
                {
                    offenders.Add($"{cmdletMatch.Groups["cmdlet"].Value}: {trimmed}");
                }
            }
        }

        _output.WriteLine($"name-based Hyper-V invocations inspected: {inspected}");

        inspected.Should().BeGreaterThan(10,
            "the orchestrator emits a known-large set of name-based Hyper-V invocations; a small " +
            "count means the scan stopped seeing the scripts and the guard is vacuous");
        offenders.Should().BeEmpty(
            "every name-based Hyper-V invocation must carry the literal -ComputerName localhost (LF-D7)");
    }

    private static readonly Regex HyperVCmdletRegex = new(
        @"(?<![\w-])(?<cmdlet>(?:Get|Set|New|Add|Remove|Start|Stop|Restart|Enable|Disable|Rename|Connect|Disconnect|Checkpoint|Import|Export|Measure|Resume|Suspend|Save)-VM[A-Za-z]*)(?![\w-])",
        RegexOptions.Compiled);

    private static readonly Regex NameBoundRegex = new(
        @"(?<![\w-])-(?:VM)?Name(?![\w-])", RegexOptions.Compiled);

    // Whole-token match so an unrelated substring (or a truncated/misspelled parameter) cannot
    // satisfy the assertion — the issue #289 bug class.
    private static readonly Regex ComputerNameLocalhostRegex = new(
        @"(?<![\w-])-ComputerName\s+localhost(?![\w-])", RegexOptions.Compiled);

    // ════════════════════════════════════════════════════════════════════
    // Orchestrator drivers
    // ════════════════════════════════════════════════════════════════════

    private static async Task RunSuccessInstallAsync(IPowerShellExecutor executor, string stagingTempPath)
    {
        var orchestrator = BuildOrchestrator(
            executor, stagingTempPath, new GuestCompletionStatus(GuestCompletionSignal.Ready, null));
        await orchestrator.InstallAsync(Request());
    }

    /// <summary>Drives the preserve-VM diagnostic path so <c>TryGetVmIdentityAsync</c> also emits.</summary>
    private static async Task RunProvisioningFailureInstallAsync(IPowerShellExecutor executor, string stagingTempPath)
    {
        var orchestrator = BuildOrchestrator(
            executor, stagingTempPath, new GuestCompletionStatus(GuestCompletionSignal.Failed, "autoinstall"));
        await Assert.ThrowsAsync<LinuxProvisionFailedException>(() => orchestrator.InstallAsync(Request()));
    }

    private static UbuntuInstallRequest Request()
        => new()
        {
            HostId = HostId,
            Name = VmName,
            IsoPath = IsoPath,
            AdminPassword = "P@ssw0rd-issue357",
            GuestUsername = "ubuntu",
            CpuCount = 2,
            MemoryMB = 4096,
            DiskSizeGB = 32,
            TimeoutMinutes = 60,
        };

    private static UbuntuAutoinstallOrchestrator BuildOrchestrator(
        IPowerShellExecutor executor, string stagingTempPath, GuestCompletionStatus completion)
    {
        var hostResolver = new Mock<IHostResolver>();
        hostResolver.Setup(resolver => resolver.ResolveRequired(It.IsAny<string?>()))
            .Returns(new HostProfile
            {
                HostId = HostId,
                ComputerName = ExpectedComputerName,
                StorageRoot = @"C:\HyperVMCP\VMs",
                DefaultSwitch = "Default Switch",
            });

        var kvpReader = new Mock<IKvpCompletionReader>();
        kvpReader.Setup(reader => reader.ReadCompletionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(completion);

        return new UbuntuAutoinstallOrchestrator(
            executor,
            kvpReader.Object,
            hostResolver.Object,
            new TestSupport.FixedTempPathProvider(stagingTempPath),
            new GuestRoutingHintStore(),
            new SeedMediaAuthor(
                executor,
                new OscdimgProbe(new SystemEnvironment()),
                NullLogger<SeedMediaAuthor>.Instance),
            NullLogger<UbuntuAutoinstallOrchestrator>.Instance);
    }
}

/// <summary>
/// One Hyper-V cmdlet invocation as PowerShell's own parameter binder resolved it.
/// <paramref name="NameBound"/> distinguishes the name-based call sites — the only ones that
/// resolve a host from COMPUTERNAME — from object/pipeline-bound ones (issue #357).
/// </summary>
internal readonly record struct ShimInvocation(string Cmdlet, string ComputerName, bool NameBound);

/// <summary>
/// Executes an orchestrator-emitted script with REAL powershell.exe, with the Hyper-V cmdlets
/// replaced by same-named shim functions that declare <c>-ComputerName</c> and swallow every other
/// argument. Binding is therefore done by PowerShell, not by a string match: a dropped or empty
/// <c>-ComputerName</c> shows up as an empty recorded value.
/// </summary>
internal sealed class HyperVShimHarness : IDisposable
{
    private readonly string _workRoot;

    public HyperVShimHarness()
    {
        _workRoot = Path.Combine(Path.GetTempPath(), "hvmcp-issue357-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workRoot);
    }

    public List<ShimInvocation> Invocations { get; } = new();

    public string Run(string script)
    {
        var logPath = Path.Combine(_workRoot, "shim-" + Guid.NewGuid().ToString("N") + ".log");
        var scriptPath = Path.Combine(_workRoot, "script-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(scriptPath, BuildPrelude(logPath) + script);

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (File.Exists(logPath))
        {
            foreach (var line in File.ReadAllLines(logPath))
            {
                var fields = line.Split('|');
                if (fields.Length == 3)
                {
                    Invocations.Add(new ShimInvocation(
                        fields[0], fields[1].Trim(), fields[2].Trim() == "name"));
                }
            }
        }

        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
        {
            // Surfaced through stdout so a harness breakage is visible in the failure text rather
            // than silently degrading into an empty recording.
            return stdout + "\nHARNESS_STDERR:" + stderr;
        }
        return stdout;
    }

    private const string VmObjectExpression =
        "New-Object PSObject -Property @{ Id = [guid]'11111111-2222-3333-4444-555555555555'; " +
        "Name = 'issue357-ubuntu-vm'; State = 'Running'; ProcessorCount = 2; MemoryStartup = 4294967296 }";

    private static string BuildPrelude(string logPath)
    {
        var escapedLogPath = logPath.Replace("'", "''");
        var shims = new[]
        {
            ("New-VM", VmObjectExpression),
            // The pre-create existence probe is the only Get-VM issued with SilentlyContinue; it
            // MUST report absence or the orchestrator short-circuits on VM_EXISTS. -ErrorAction is
            // a common parameter, so it surfaces here as the local $ErrorActionPreference.
            ("Get-VM", "if ($ErrorActionPreference -eq 'SilentlyContinue') { $null } else { " + VmObjectExpression + " }"),
            ("Set-VM", "$null"),
            ("Set-VMProcessor", "$null"),
            ("Set-VMFirmware", "$null"),
            ("Add-VMDvdDrive", "$null"),
            ("Get-VMDvdDrive", "New-Object PSObject -Property @{ Path = 'C:\\ISOs\\ubuntu-24.04-live-server-amd64.iso' }"),
            ("Remove-VMDvdDrive", "$null"),
            ("Enable-VMIntegrationService", "$null"),
            ("Start-VM", "$null"),
            ("Get-VMNetworkAdapter", "New-Object PSObject -Property @{ IPAddresses = @('192.168.1.50') }"),
        };

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("$script:HvmcpShimLog = '" + escapedLogPath + "'");
        builder.AppendLine("function Write-HvmcpShimRecord { param([string]$Cmdlet, [string]$ComputerName, [string]$Binding)");
        builder.AppendLine("  Add-Content -LiteralPath $script:HvmcpShimLog -Value ($Cmdlet + '|' + $ComputerName + '|' + $Binding) }");

        foreach (var (cmdlet, returnExpression) in shims)
        {
            builder.AppendLine("function global:" + cmdlet + " {");
            builder.AppendLine("  [CmdletBinding()] param(");
            builder.AppendLine("    [Parameter(ValueFromPipeline=$true)] $InputObject,");
            builder.AppendLine("    [string] $ComputerName,");
            builder.AppendLine("    [string] $Name,");
            builder.AppendLine("    [string] $VMName,");
            builder.AppendLine("    [Parameter(ValueFromRemainingArguments=$true)] $Rest)");
            builder.AppendLine("  process {");
            builder.AppendLine("    $bound = if ($Name -or $VMName) { 'name' } else { 'object' }");
            builder.AppendLine("    Write-HvmcpShimRecord -Cmdlet '" + cmdlet + "' -ComputerName $ComputerName -Binding $bound");
            builder.AppendLine("    " + returnExpression);
            builder.AppendLine("  } }");
        }

        return builder.ToString();
    }

    public void Dispose()
    {
        try { Directory.Delete(_workRoot, recursive: true); } catch { /* best-effort */ }
    }
}

/// <summary>
/// <see cref="IPowerShellExecutor"/> that records every emitted script and runs the Hyper-V-bearing
/// ones through <see cref="HyperVShimHarness"/>. Seed staging and media authoring return canned
/// success — those paths have their own real-seam coverage (issue #292) and carry no Hyper-V cmdlet.
/// </summary>
internal sealed class HarnessBackedPowerShellExecutor : IPowerShellExecutor
{
    private readonly HyperVShimHarness _harness;

    public HarnessBackedPowerShellExecutor(HyperVShimHarness harness) => _harness = harness;

    public List<string> Scripts { get; } = new();

    public Task<PowerShellResult> ExecuteAsync(
        string script, int timeoutSeconds = 300, CancellationToken ct = default, bool allowDump = true)
    {
        Scripts.Add(script);

        if (script.Contains("autoinstall:", StringComparison.Ordinal))
        {
            return Task.FromResult(new PowerShellResult { ExitCode = 0, Stdout = "STAGE_OK" });
        }
        if (script.Contains("SEED_AUTHOR=", StringComparison.Ordinal))
        {
            return Task.FromResult(new PowerShellResult { ExitCode = 0, Stdout = "SEED_AUTHOR=imapi2" });
        }

        var stdout = _harness.Run(script);
        return Task.FromResult(new PowerShellResult { ExitCode = 0, Stdout = stdout });
    }

    public Task<PowerShellResult> ExecuteWithSecretsAsync(
        string script,
        IReadOnlyDictionary<string, string> secretEnvironment,
        int timeoutSeconds = 300,
        CancellationToken ct = default)
        => ExecuteAsync(script, timeoutSeconds, ct);
}
