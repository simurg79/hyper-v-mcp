using System.Globalization;
using System.Management.Automation;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using HyperV.Mcp.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace HyperV.Mcp.Server.Tests.Runtime;

[Trait("Category", "Runtime")]
public class Issue382UbuntuManagedTagRealSeamTests
{
    private const string VmName = "issue382-ubuntu-vm";
    private const string VmId = "11111111-2222-3333-4444-555555555555";
    private readonly ITestOutputHelper _output;

    public Issue382UbuntuManagedTagRealSeamTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Creation_StampsExactTagImmediatelyAfterNewVm_WithExplicitStopAndLocalhost()
    {
        using var executor = new ScriptExecutor();
        await BuildOrchestrator(executor).InstallAsync(Request());

        var creationScript = executor.Scripts.Single(script => script.Contains("New-VM -Name"));
        var tagLine = Assert.Single(creationScript.Split('\n'), line => line.Contains("-Notes"));
        tagLine.Should().Contain(
            "Set-VM -Name $name -Notes \"hyper-v-mcp:created=$(Get-Date -Format o);type=iso-install;role=ephemeral\"");
        tagLine.Should().Contain("-ComputerName localhost").And.Contain("-ErrorAction Stop");
        var invocations = executor.ReadLines("$script:Invocations");
        var creationIndex = Array.IndexOf(invocations, "New-VM");
        creationIndex.Should().BeGreaterThanOrEqualTo(0);
        invocations[creationIndex + 1].Should().Be("Set-VM");
        invocations[creationIndex + 2].Should().Be("Set-VMProcessor");
        Array.IndexOf(invocations, "Start-VM").Should().BeGreaterThan(creationIndex + 2);
        executor.ReadLines("$script:TagErrorAction").Should().Equal("Stop");
        executor.ReadLines("$script:TagComputerName").Should().Equal("localhost");

        var notes = Assert.Single(executor.ReadLines("$script:CreatedVm.Notes"));
        var tagMatch = Regex.Match(notes,
            @"\Ahyper-v-mcp:created=(?<created>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}(?:Z|[+-]\d{2}:\d{2}));type=iso-install;role=ephemeral\z");
        tagMatch.Success.Should().BeTrue($"the bound Notes must use the Windows ISO-install format: {notes}");
        DateTimeOffset.TryParseExact(tagMatch.Groups["created"].Value, "o", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _).Should().BeTrue();
        _output.WriteLine($"Bound Notes: {notes}");
        _output.WriteLine("Order: New-VM -> Set-VM -> Set-VMProcessor -> configuration -> Start-VM; ErrorAction=Stop");
    }

    [Theory]
    [InlineData("Notes write denied")]
    [InlineData("VM with name already exists in Notes service")]
    public async Task TagWriteFailure_StopsWithoutConfigurationStartRetryOrRemoval_AndNamesUnlistedVm(string cause)
    {
        using var executor = new ScriptExecutor(failingCommand: "Set-VM", failureCause: cause);
        var exception = await Assert.ThrowsAsync<LinuxProvisionFailedException>(
            () => BuildOrchestrator(executor).InstallAsync(Request()));
        var response = new ErrorMapper().MapException(exception);

        response.ErrorCode.Should().Be("LINUX_PROVISION_FAILED");
        response.Error.Should().Contain(VmName).And.Contain("untagged").And.Contain("absent from the managed list");
        response.Error.Should().Contain(cause);
        exception.VmName.Should().Be(VmName);
        executor.ReadLines("$script:Invocations").Should().Equal("Get-VM", "New-VM", "Set-VM");
        executor.ReadLines("$script:CreatedVm.Id").Should().Equal(VmId);
        executor.ReadLines("$script:CreatedVm.Notes").Should().Equal("");
        (await BuildManager(executor).ListVmsAsync("local", VmName)).Should().BeEmpty();
        _output.WriteLine($"{response.ErrorCode}: {response.Error}");
        _output.WriteLine("Executed: Get-VM, New-VM, Set-VM; preserved; no configuration, start, retry or removal.");
    }

    [Fact]
    public async Task PostTagConfigurationFailure_ErrorEnvelopeCarriesCreatedVmId()
    {
        using var executor = new ScriptExecutor(failingCommand: "Set-VMProcessor");
        var exception = await Assert.ThrowsAsync<LinuxProvisionFailedException>(
            () => BuildOrchestrator(executor).InstallAsync(Request()));
        var response = new ErrorMapper().MapException(exception);
        var details = JsonSerializer.SerializeToElement(response.Details);

        response.ErrorCode.Should().Be("LINUX_PROVISION_FAILED");
        exception.VmId.Should().Be(VmId);
        details.GetProperty("vmId").GetString().Should().Be(VmId);
        details.GetProperty("failingStep").GetString().Should().Be("vm-create");
        executor.ReadLines("$script:CreatedVm.Id").Should().Equal(VmId);
        executor.ReadLines("$script:CreatedVm.Notes").Should().ContainSingle()
            .Which.Should().StartWith("hyper-v-mcp:created=");
        executor.ReadLines("$script:Invocations").Should().Equal("Get-VM", "New-VM", "Set-VM", "Set-VMProcessor");
        _output.WriteLine($"Post-tag failure: exception.vmId={exception.VmId}; details={details}");
    }

    [Fact]
    public async Task TagWriteFailure_ErrorEnvelopeCarriesCreatedVmId()
    {
        using var executor = new ScriptExecutor(failingCommand: "Set-VM");
        var exception = await Assert.ThrowsAsync<LinuxProvisionFailedException>(
            () => BuildOrchestrator(executor).InstallAsync(Request()));
        var response = new ErrorMapper().MapException(exception);
        var details = JsonSerializer.SerializeToElement(response.Details);

        response.ErrorCode.Should().Be("LINUX_PROVISION_FAILED");
        exception.VmId.Should().Be(VmId);
        details.GetProperty("vmId").GetString().Should().Be(VmId);
        details.GetProperty("failingStep").GetString().Should().Be("vm-create");
        executor.ReadLines("$script:CreatedVm.Id").Should().Equal(VmId);
        executor.ReadLines("$script:CreatedVm.Notes").Should().Equal("");
        executor.ReadLines("$script:Invocations").Should().Equal("Get-VM", "New-VM", "Set-VM");
        _output.WriteLine($"Tag-write failure: exception.vmId={exception.VmId}; details={details}");
    }

    [Fact]
    public async Task PostCreationNameCollisionText_StaysProvisionFailedWithCreatedVmId()
    {
        using var executor = new ScriptExecutor(failingCommand: "Set-VMProcessor",
            failureCause: "VM with name already exists in processor service");
        var exception = await Assert.ThrowsAsync<LinuxProvisionFailedException>(
            () => BuildOrchestrator(executor).InstallAsync(Request()));

        exception.VmId.Should().Be(VmId);
        executor.ReadLines("$script:Invocations").Should().NotContain("Remove-VM");
    }

    [Theory]
    [InlineData("configuration-failure", null)]
    [InlineData("provisioning-failure", VmId)]
    [InlineData("timeout", VmId)]
    public async Task MalformedCreatedIdMarker_IsRejected_AndFallbackIdentityApplies(
        string outcome, string? expectedVmId)
    {
        using var executor = new ScriptExecutor(
            failingCommand: outcome == "configuration-failure" ? "Set-VMProcessor" : null,
            markerId: "not-a-guid");
        var orchestrator = BuildOrchestrator(executor,
            outcome == "provisioning-failure" ? GuestCompletionSignal.Failed : GuestCompletionSignal.Ready);

        string? reportedVmId = outcome == "timeout"
            ? (await Assert.ThrowsAsync<LinuxInstallTimeoutException>(
                () => orchestrator.InstallAsync(Request(timeoutMinutes: 0)))).VmId
            : (await Assert.ThrowsAsync<LinuxProvisionFailedException>(
                () => orchestrator.InstallAsync(Request()))).VmId;

        reportedVmId.Should().Be(expectedVmId);
        _output.WriteLine($"{outcome}: malformed marker -> vmId={reportedVmId ?? "null"}");
    }

    // Renaming or removal can break the later name lookup, so retain the marker ID.
    [Theory]
    [InlineData("provisioning-failure")]
    [InlineData("timeout")]
    public async Task CreatedVmId_ComesFromMarker_WhenLaterNameLookupMisses(string outcome)
    {
        using var executor = new ScriptExecutor(loseVmAfterCreation: true);
        var orchestrator = BuildOrchestrator(executor,
            outcome == "provisioning-failure" ? GuestCompletionSignal.Failed : GuestCompletionSignal.Ready);

        string? reportedVmId = outcome == "timeout"
            ? (await Assert.ThrowsAsync<LinuxInstallTimeoutException>(
                () => orchestrator.InstallAsync(Request(timeoutMinutes: 0)))).VmId
            : (await Assert.ThrowsAsync<LinuxProvisionFailedException>(
                () => orchestrator.InstallAsync(Request()))).VmId;

        reportedVmId.Should().Be(VmId);
    }

    [Fact]
    public async Task TagWriteFailureWithCollisionText_AndMalformedMarker_StaysProvisionFailed()
    {
        using var executor = new ScriptExecutor(failingCommand: "Set-VM",
            failureCause: "VM with name already exists in Notes service", markerId: "not-a-guid");
        var exception = await Assert.ThrowsAsync<LinuxProvisionFailedException>(
            () => BuildOrchestrator(executor).InstallAsync(Request()));

        exception.VmId.Should().BeNull();
        exception.Message.Should().Contain("untagged");
    }

    [Theory]
    [InlineData("success")]
    [InlineData("timeout")]
    [InlineData("provisioning-failure")]
    [InlineData("configuration-failure")]
    public async Task CreatedVm_RealListPredicateIncludesPreservedVm_AndDoesNotAdoptForeignVms(string outcome)
    {
        using var executor = new ScriptExecutor(
            failingCommand: outcome == "configuration-failure" ? "Set-VMProcessor" : null);
        var orchestrator = BuildOrchestrator(executor,
            outcome == "provisioning-failure" ? GuestCompletionSignal.Failed : GuestCompletionSignal.Ready);
        string? reportedVmId;
        if (outcome == "timeout")
        {
            var exception = await Assert.ThrowsAsync<LinuxInstallTimeoutException>(
                () => orchestrator.InstallAsync(Request(timeoutMinutes: 0)));
            reportedVmId = exception.VmId;
        }
        else if (outcome.EndsWith("failure", StringComparison.Ordinal))
        {
            var exception = await Assert.ThrowsAsync<LinuxProvisionFailedException>(
                () => orchestrator.InstallAsync(Request()));
            exception.VmName.Should().Be(VmName);
            reportedVmId = exception.VmId;
        }
        else
        {
            reportedVmId = (await orchestrator.InstallAsync(Request())).VmId;
        }

        var manager = BuildManager(executor);
        var allVms = await manager.ListVmsAsync("local");
        allVms.Select(vm => vm.Name).Should().BeEquivalentTo(VmName, "windows-managed");
        var matching = await manager.ListVmsAsync("local", VmName);
        matching.Should().ContainSingle().Which.VmId.Should().Be(VmId);
        matching[0].VmId.Should().Be(reportedVmId);
        (await manager.ListVmsAsync("local", "nonmatching")).Should().BeEmpty();
        executor.ReadLines("$script:ForeignVm.Notes").Should().Equal("");
        executor.ReadLines("$script:Invocations").Should().NotContain("Remove-VM");
        executor.ReadLines("$script:Invocations").Count(command => command == "Set-VM").Should().Be(1);
        _output.WriteLine($"{outcome}: real ListVmsAsync filter includes {VmId}; foreign VM excluded and unchanged.");
    }

    [Fact]
    public async Task ExistingName_IsRejectedBeforeCreationOrTagging()
    {
        using var executor = new ScriptExecutor(existingName: true);
        await Assert.ThrowsAsync<VmAlreadyExistsException>(
            () => BuildOrchestrator(executor).InstallAsync(Request()));
        executor.ReadLines("$script:Invocations").Should().Equal("Get-VM");
        executor.ReadLines("$script:ForeignVm.Notes").Should().Equal("");
    }

    private static UbuntuInstallRequest Request(int timeoutMinutes = 1) => new()
    {
        HostId = "local",
        Name = VmName,
        IsoPath = @"C:\ISOs\ubuntu-prepared.iso",
        AdminPassword = "issue382-test-password",
        GuestUsername = "ubuntu",
        CpuCount = 2,
        MemoryMB = 4096,
        DiskSizeGB = 32,
        TimeoutMinutes = timeoutMinutes,
    };

    private static IHostResolver HostResolver()
    {
        var resolver = new Mock<IHostResolver>();
        resolver.Setup(host => host.ResolveRequired(It.IsAny<string?>())).Returns(new HostProfile
        {
            HostId = "local", ComputerName = "localhost", StorageRoot = @"C:\HyperVMCP\VMs",
        });
        return resolver.Object;
    }

    private static UbuntuAutoinstallOrchestrator BuildOrchestrator(
        ScriptExecutor executor, GuestCompletionSignal signal = GuestCompletionSignal.Ready)
    {
        var completionReader = new Mock<IKvpCompletionReader>();
        completionReader.Setup(reader => reader.ReadCompletionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestCompletionStatus(signal, signal == GuestCompletionSignal.Failed ? "autoinstall" : null));
        var author = new Mock<ISeedMediaAuthor>();
        author.Setup(media => media.AuthorAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeedMediaAuthoringResult(MediaAuthoringRoute.Imapi2, true, null));
        return new UbuntuAutoinstallOrchestrator(executor, completionReader.Object, HostResolver(),
            new FixedTempPathProvider(Path.GetTempPath()), new GuestRoutingHintStore(), author.Object,
            NullLogger<UbuntuAutoinstallOrchestrator>.Instance);
    }

    private static HyperVManager BuildManager(ScriptExecutor executor) => new(
        executor, HostResolver(), new ServerOptions(), NullLogger<HyperVManager>.Instance,
        Mock.Of<IIsoInspector>());

    // Only host cmdlets and unrelated seed work are substituted so PowerShell controls
    // creation/listing order, error propagation and filtering.
    private sealed class ScriptExecutor : IPowerShellExecutor, IDisposable
    {
        private readonly PowerShell _powershell = PowerShell.Create();
        public List<string> Scripts { get; } = new();

        public ScriptExecutor(string? failingCommand = null, string failureCause = "Configuration denied",
            bool existingName = false, string? markerId = null, bool loseVmAfterCreation = false)
        {
            _loseVmAfterCreation = loseVmAfterCreation;
            _powershell.Runspace.SessionStateProxy.SetVariable("MarkerId", markerId);
            _powershell.Runspace.SessionStateProxy.SetVariable("FailureCommand", failingCommand);
            _powershell.Runspace.SessionStateProxy.SetVariable("FailureCause", failureCause);
            _powershell.Runspace.SessionStateProxy.SetVariable("ExistingName", existingName);
            var initialized = Invoke(Prelude);
            Assert.True(initialized.Success, initialized.Stderr);
        }

        public Task<PowerShellResult> ExecuteAsync(
            string script, int timeoutSeconds = 300, CancellationToken ct = default, bool allowDump = true)
        {
            Scripts.Add(script);
            if (script.Contains("autoinstall:", StringComparison.Ordinal))
                return Task.FromResult(new PowerShellResult { ExitCode = 0, Stdout = "STAGE_OK" });
            var result = Invoke(script);
            if (_loseVmAfterCreation && script.Contains("New-VM -Name", StringComparison.Ordinal))
                Invoke("$script:CreatedVm = $null");
            return Task.FromResult(result);
        }

        private readonly bool _loseVmAfterCreation;

        public string[] ReadLines(string expression)
        {
            var result = Invoke(expression);
            Assert.True(result.Success, result.Stderr);
            return result.Stdout.Split('\n');
        }

        private PowerShellResult Invoke(string script)
        {
            _powershell.Commands.Clear();
            _powershell.Streams.Error.Clear();
            var output = new System.Management.Automation.PSDataCollection<PSObject>();
            string? terminatingError = null;
            try
            {
                _powershell.AddScript(script).Invoke<PSObject, PSObject>(null, output, null);
            }
            catch (RuntimeException exception)
            {
                terminatingError = exception.Message;
            }
            return new PowerShellResult
            {
                ExitCode = _powershell.HadErrors || terminatingError is not null ? 1 : 0,
                Stdout = string.Join("\n", output.Select(value => value.ToString())),
                Stderr = string.Join("\n", _powershell.Streams.Error.Select(error => error.ToString()))
                    + (terminatingError is null ? "" : "\n" + terminatingError),
            };
        }

        public void Dispose() => _powershell.Dispose();

        private const string Prelude = """
Import-Module Microsoft.PowerShell.Utility, Microsoft.PowerShell.Management -ErrorAction Stop
$PSModuleAutoLoadingPreference = 'None'
$script:Invocations = [System.Collections.Generic.List[string]]::new()
$script:CreatedVm = $null
$script:ForeignVm = [pscustomobject]@{ Id = 'foreign'; Name = 'issue382-ubuntu-vm-foreign'; Notes = '' }
$script:WindowsVm = [pscustomobject]@{
    Id = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'; Name = 'windows-managed'
    Notes = 'hyper-v-mcp:created=2026-01-01T00:00:00.0000000+00:00;type=iso-install;role=ephemeral'
    State = 'Off'; ProcessorCount = 2; MemoryStartup = 4GB; Uptime = [timespan]::Zero
}
function Record-Invocation([string]$Command) { $script:Invocations.Add($Command) }
function Import-Module { param($Name, $ErrorAction) }
function New-Item { param($ItemType, [switch]$Force, $Path) }
function Get-VM {
    [CmdletBinding()] param($Name, $ComputerName)
    Record-Invocation 'Get-VM'
    if ($Name.Contains('*')) {
        @($script:CreatedVm, $script:ForeignVm, $script:WindowsVm) |
            Where-Object { $null -ne $_ -and $_.Name -like $Name }
    } elseif ($null -ne $script:CreatedVm) { $script:CreatedVm }
    elseif ($ExistingName) { $script:ForeignVm }
}
function New-VM {
    [CmdletBinding()] param($Name, $Generation, $MemoryStartupBytes, $SwitchName, $Path,
        $NewVHDPath, $NewVHDSizeBytes, $ComputerName)
    Record-Invocation 'New-VM'
    $script:CreatedVm = [pscustomobject]@{
        Id = [guid]'11111111-2222-3333-4444-555555555555'; Name = $Name; Notes = ''; State = 'Off'
        ProcessorCount = 2; MemoryStartup = $MemoryStartupBytes; Uptime = [timespan]::Zero
    }
    if ($MarkerId) { [pscustomobject]@{ Id = $MarkerId } } else { $script:CreatedVm }
}
function Set-VM {
    [CmdletBinding()] param($Name, $Notes, $ComputerName)
    Record-Invocation 'Set-VM'
    $script:TagErrorAction = [string]$PSBoundParameters['ErrorAction']
    $script:TagComputerName = $ComputerName
    if ($FailureCommand -eq 'Set-VM') { Write-Error $FailureCause; return }
    $script:CreatedVm.Notes = $Notes
}
function Set-VMProcessor {
    [CmdletBinding()] param($VMName, $Count, $ComputerName)
    Record-Invocation 'Set-VMProcessor'
    if ($FailureCommand -eq 'Set-VMProcessor') { Write-Error $FailureCause }
}
function Set-VMFirmware {
    [CmdletBinding()] param($VMName, $EnableSecureBoot, $FirstBootDevice, $ComputerName)
    Record-Invocation 'Set-VMFirmware'
}
function Add-VMDvdDrive {
    [CmdletBinding()] param($VMName, $Path, $ComputerName)
    Record-Invocation 'Add-VMDvdDrive'
}
function Get-VMDvdDrive {
    [CmdletBinding()] param($VMName, $ComputerName)
    Record-Invocation 'Get-VMDvdDrive'
    [pscustomobject]@{ Path = 'C:\ISOs\ubuntu-prepared.iso' }
}
function Enable-VMIntegrationService {
    [CmdletBinding()] param($VMName, $Name, $ComputerName)
    Record-Invocation 'Enable-VMIntegrationService'
}
function Start-VM {
    [CmdletBinding()] param($Name, $ComputerName)
    Record-Invocation 'Start-VM'
    $script:CreatedVm.State = 'Running'
}
function Remove-VMDvdDrive {
    [CmdletBinding()] param([Parameter(ValueFromPipeline=$true)]$InputObject)
    process { Record-Invocation 'Remove-VMDvdDrive' }
}
function Get-VMNetworkAdapter {
    [CmdletBinding()] param($VMName, $ComputerName)
    Record-Invocation 'Get-VMNetworkAdapter'
    [pscustomobject]@{ IPAddresses = @('192.0.2.38') }
}
function Remove-VM {
    [CmdletBinding()] param($Name, $ComputerName, [switch]$Force)
    Record-Invocation 'Remove-VM'
    throw 'VM removal is forbidden'
}
""";
    }
}
