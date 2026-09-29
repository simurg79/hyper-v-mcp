using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Drives an unattended Ubuntu Server 24.04 install (ISO-D29 + Appendix A). Creates a
/// Generation-2 / Secure-Boot-off VM (ISO-D25), delivers cloud-init autoinstall via a NoCloud
/// <c>CIDATA</c> seed ISO on a second DVD (ISO-D24/D29), detects completion via the guest KVP
/// (ISO-D26), and returns the shared <see cref="OsInstallResult"/> (FR-14). Modeled as an
/// interface so the Tester can drive deterministic terminal outcomes without a real VM.
/// See myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md — ISO-D24..D30.
/// </summary>
public interface IUbuntuAutoinstallOrchestrator
{
    /// <summary>
    /// Installs Ubuntu Server 24.04 into a new Gen-2 VM named <paramref name="name"/>.
    /// On timeout or observed provisioning failure the VM is PRESERVED (ISO-D28); the seed
    /// artifact is deleted on every path (ISO-D29).
    /// </summary>
    Task<OsInstallResult> InstallAsync(UbuntuInstallRequest request, CancellationToken ct = default);
}

/// <summary>
/// Immutable inputs for one Ubuntu install. <see cref="AdminPassword"/> is the plaintext that is
/// SHA-512-crypt hashed into the seed and NEVER written to disk or logs (ISO-D24 / FR-18).
/// </summary>
public sealed class UbuntuInstallRequest
{
    public required string HostId { get; init; }
    public required string Name { get; init; }
    public required string IsoPath { get; init; }
    public required string AdminPassword { get; init; }
    public required string GuestUsername { get; init; }
    public int CpuCount { get; init; }
    public long MemoryMB { get; init; }
    public int DiskSizeGB { get; init; }
    public string? SwitchName { get; init; }
    public string Locale { get; init; } = "en-US";
    public int TimeoutMinutes { get; init; } = 60;

    /// <summary>
    /// Caller-requested VM generation, when explicitly supplied. Ubuntu requires Gen 2; a value
    /// other than 2 fails fast as <c>LINUX_PRECONDITION_UNMET</c> (ISO-D25). Null = server default.
    /// </summary>
    public int? RequestedGeneration { get; init; }

    /// <summary>
    /// Caller-requested Secure Boot state, when explicitly supplied. Ubuntu requires Secure Boot
    /// off; an explicit <c>true</c> fails fast as <c>LINUX_PRECONDITION_UNMET</c> (ISO-D25).
    /// </summary>
    public bool? RequestedSecureBoot { get; init; }
}

/// <summary>
/// PowerShell/WMI-backed <see cref="IUbuntuAutoinstallOrchestrator"/>.
/// See myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md — Appendix A.3/A.4.
/// </summary>
public sealed class UbuntuAutoinstallOrchestrator : IUbuntuAutoinstallOrchestrator
{
    private const string DefaultStorageRoot = @"C:\HyperVMCP\VMs";

    // Poll cadence for the completion KVP; aligned with the async-job loop (~15 s, Appendix A.4).
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    /// <summary>Stable step identifiers surfaced as <c>details.failingStep</c> (ISO-D37).</summary>
    internal const string StepSeedMediaBuild = "seed-media-build";
    internal const string StepVmCreate = "vm-create";
    internal const string StepProvisioningWait = "provisioning-wait";

    private const string SeedVolumeLabel = "CIDATA";

    /// <summary>
    /// Guest-side writer for the completion KVP. Emits the fixed-width record framing that
    /// <c>hv_kvp_daemon</c> requires (512-byte key + 2048-byte value, NUL padded), replacing any
    /// prior record for the same key so repeated boots stay idempotent (issue #321).
    /// </summary>
    private const string KvpSignalScript = @"#!/usr/bin/env python3
import os
POOL = '/var/lib/hyperv/.kvp_pool_1'
KEY = b'hyperv-mcp/os-install'
VALUE = b'ready'
RECORD = 2560
os.makedirs('/var/lib/hyperv', exist_ok=True)
record = KEY.ljust(512, b'\x00') + VALUE.ljust(2048, b'\x00')
existing = b''
if os.path.exists(POOL):
    with open(POOL, 'rb') as handle:
        existing = handle.read()
kept = b''
for offset in range(0, len(existing) - RECORD + 1, RECORD):
    chunk = existing[offset:offset + RECORD]
    if chunk[:512].rstrip(b'\x00') != KEY:
        kept += chunk
with open(POOL, 'wb') as handle:
    handle.write(kept + record)
";

    /// <summary>
    /// First-boot oneshot that publishes the completion KVP. Ordered after the KVP daemon so the
    /// pool directory exists and the daemon picks the record up on its next scan.
    /// </summary>
    private const string KvpSignalUnit = @"[Unit]
Description=Publish hyperv-mcp install completion via KVP
After=hv-kvp-daemon.service
Wants=hv-kvp-daemon.service

[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=/usr/local/sbin/hyperv-mcp-signal-ready

[Install]
WantedBy=multi-user.target
";

    private readonly IPowerShellExecutor _psExecutor;
    private readonly IKvpCompletionReader _kvpReader;
    private readonly IHostResolver _hostResolver;
    private readonly ITempPathProvider _tempPathProvider;
    private readonly IGuestRoutingHintStore _hintStore;
    private readonly ISeedMediaAuthor _seedMediaAuthor;
    private readonly ILogger<UbuntuAutoinstallOrchestrator> _logger;

    public UbuntuAutoinstallOrchestrator(
        IPowerShellExecutor psExecutor,
        IKvpCompletionReader kvpReader,
        IHostResolver hostResolver,
        ITempPathProvider tempPathProvider,
        IGuestRoutingHintStore hintStore,
        ISeedMediaAuthor seedMediaAuthor,
        ILogger<UbuntuAutoinstallOrchestrator> logger)
    {
        _psExecutor = psExecutor ?? throw new ArgumentNullException(nameof(psExecutor));
        _kvpReader = kvpReader ?? throw new ArgumentNullException(nameof(kvpReader));
        _hostResolver = hostResolver ?? throw new ArgumentNullException(nameof(hostResolver));
        _tempPathProvider = tempPathProvider ?? throw new ArgumentNullException(nameof(tempPathProvider));
        _hintStore = hintStore ?? throw new ArgumentNullException(nameof(hintStore));
        _seedMediaAuthor = seedMediaAuthor ?? throw new ArgumentNullException(nameof(seedMediaAuthor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<OsInstallResult> InstallAsync(UbuntuInstallRequest request, CancellationToken ct = default)
    {
        // Fail fast before any VM or job exists, and only when the caller EXPLICITLY contradicts
        // Gen2 + Secure-Boot-off; otherwise the server sets them by construction below.
        // See myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md — ISO-D25.
        EnforceFirmwarePrecondition(request);

        var startTime = DateTime.UtcNow;

        // Stage the seed under a dedicated per-install directory so cleanup is a single rmdir.
        var stagingRoot = Path.Combine(
            _tempPathProvider.GetTempPath(),
            "hvmcp-ubuntu-seed-" + Guid.NewGuid().ToString("N"));
        var seedIsoPath = Path.Combine(stagingRoot, "cidata.iso");

        try
        {
            // Seed authoring MUST stay ahead of VM creation: a media-prep failure is then terminal
            // with no VM to preserve. See
            // myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md — ISO-D36.
            await BuildSeedIsoAsync(request, stagingRoot, seedIsoPath, ct).ConfigureAwait(false);

            var createdVmId = await CreateVmAndAttachMediaAsync(request, seedIsoPath, ct).ConfigureAwait(false);

            var completion = await WaitForCompletionAsync(request, ct).ConfigureAwait(false);

            var elapsedSeconds = (int)(DateTime.UtcNow - startTime).TotalSeconds;

            if (completion.Signal == GuestCompletionSignal.Failed)
            {
                // The VM is deliberately left in place for inspection; only the seed is cleaned.
                _logger.LogWarning(
                    "Ubuntu autoinstall reported provisioning failure for VM '{VmName}' (detail={Detail}); VM preserved.",
                    request.Name, completion.Detail ?? "unspecified");
                var (failVmId, _, _) = await TryGetVmIdentityAsync(request, ct).ConfigureAwait(false);
                var (waitCause, waitCauseTruncated) =
                    ErrorMapper.PrepareDiagnosticCause(completion.Detail, request.AdminPassword);
                throw new LinuxProvisionFailedException(
                    ComposeFailureMessage(
                        "Ubuntu autoinstall failed during provisioning; the VM has been preserved for inspection.",
                        StepProvisioningWait, waitCause),
                    failingStep: StepProvisioningWait,
                    vmId: createdVmId ?? failVmId, vmName: request.Name,
                    cause: waitCause, causeTruncated: waitCauseTruncated);
            }

            if (completion.Signal != GuestCompletionSignal.Ready)
            {
                // Deadline without a ready signal: timeout, and the VM is left for inspection.
                _logger.LogWarning(
                    "Ubuntu autoinstall did not signal completion within {TimeoutMinutes} min for VM '{VmName}'; VM preserved.",
                    request.TimeoutMinutes, request.Name);

                // Triage context is gathered ONCE, here at the deadline — not per poll, which would
                // add a host query every 15 s for an hour to serve a single terminal error.
                // See myplans/vm-management/iso-installation/ubuntu-media-capability-and-timeout-diagnostics-design.md
                // — UMD-D6, UMD-D7.
                var (timeoutVmId, _, attachedIsoPath) =
                    await TryGetVmIdentityAsync(request, ct).ConfigureAwait(false);
                var channelState = await ProbeChannelStateAsync(request, ct).ConfigureAwait(false);

                throw new LinuxInstallTimeoutException(
                    ComposeTimeoutMessage(
                        request.TimeoutMinutes, attachedIsoPath, channelState,
                        rawSummary: null, password: request.AdminPassword),
                    timeoutMinutes: request.TimeoutMinutes,
                    vmId: createdVmId ?? timeoutVmId,
                    vmName: request.Name,
                    attachedIsoPath: attachedIsoPath,
                    channelState: channelState);
            }

            // Success: ensure the install DVDs are ejected and the VM is left running/usable.
            var identity = await FinalizeAndDescribeAsync(request, ct).ConfigureAwait(false);
            identity.InstallationDurationSeconds = elapsedSeconds;
            identity.TotalDurationSeconds = elapsedSeconds;

            // Record the per-VM Linux hint so later guest calls route to SSH; the endpoint is the
            // finalize-resolved guest IP on port 22. No credential is stored, and OsInstallResult
            // is unchanged.
            // See myplans/remoting/linux-guest-support/linux-guest-routing-design.md — LGR-D2.
            if (!string.IsNullOrWhiteSpace(identity.VmId))
            {
                _hintStore.Record(
                    request.HostId,
                    identity.VmId,
                    new GuestRoutingHint(GuestOsKind.Linux, identity.GuestIpAddress, 22));
            }

            return identity;
        }
        finally
        {
            // Delete the seed (it holds the hashed credential) on EVERY path so the
            // credential-at-rest window stays bounded.
            TryDeleteStaging(stagingRoot);
        }
    }

    /// <summary>
    /// Rejects only an explicit caller request that contradicts Ubuntu's Gen-2 + Secure-Boot-off
    /// requirement. Synchronous — no VM or job exists yet.
    /// </summary>
    private static void EnforceFirmwarePrecondition(UbuntuInstallRequest request)
    {
        if (request.RequestedGeneration is int generation && generation != 2)
        {
            throw new LinuxPreconditionUnmetException(
                "Ubuntu Server 24.04 requires a Generation 2 VM; the requested generation is incompatible.");
        }
        if (request.RequestedSecureBoot == true)
        {
            throw new LinuxPreconditionUnmetException(
                "Ubuntu Server 24.04 requires Secure Boot to be off; the requested configuration enables it.");
        }
    }

    /// <summary>
    /// Writes <c>user-data</c>/<c>meta-data</c>/<c>vendor-data</c> and packages them into a
    /// <c>CIDATA</c>-labeled ISO.
    ///
    /// The SHA-512-crypt hash is computed in managed memory (<see cref="Sha512Crypt"/>) before the
    /// script is built, so only the <c>$6$…</c> hash — never the plaintext — is interpolated. The
    /// plaintext never reaches the temp <c>.ps1</c> that <see cref="PowerShellExecutor"/> writes to
    /// disk, nor logs, KVP, or status. Hashing in-process also drops the OpenSSL host prerequisite.
    /// See myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md — ISO-D29 / ISO-D31.
    /// </summary>
    private async Task BuildSeedIsoAsync(
        UbuntuInstallRequest request, string stagingRoot, string seedIsoPath, CancellationToken ct)
    {
        // Managed SHA-512-crypt: plaintext stays in this process's memory and is never emitted.
        var passwordHash = Sha512Crypt.Hash(request.AdminPassword);

        var escapedStagingRoot = InputValidation.EscapePowerShellString(stagingRoot);
        var escapedPasswordHash = InputValidation.EscapePowerShellString(passwordHash);
        var escapedUsername = InputValidation.EscapePowerShellString(request.GuestUsername);
        var escapedHostname = InputValidation.EscapePowerShellString(request.Name);
        var escapedLocale = InputValidation.EscapePowerShellString(request.Locale);

        // The script only ever sees the already-hashed credential ($6$…), so it is safe for the
        // temp-file executor path and the (disabled here) dump masker alike.
        //
        // The guest→host completion signal (ISO-D26) is delivered by a first-boot systemd oneshot
        // that writes a CORRECTLY FRAMED record into /var/lib/hyperv/.kvp_pool_1.
        //
        // WHY not a plain `printf` into that file (issue #321, Assumption 1 REFUTED): the pool is
        // not a text sink. hv_kvp_daemon reads fixed-width 2560-byte records — a 512-byte NUL
        // padded key followed by a 2048-byte NUL padded value. A bare "key value" string is
        // unparseable, so the host never observes the key and even a perfect install times out.
        //
        // WHY first boot instead of late-commands: the KVP daemon runs in the INSTALLED system,
        // not the installer environment, and writing into /target during install would race the
        // daemon's own pool initialization on first start.
        //
        // Payloads are base64-embedded so no shell/YAML/PowerShell quoting layer can corrupt the
        // NUL-sensitive writer or the unit file.
        // CRLF MUST be normalized to LF: a C# verbatim string carries this source file's CRLF
        // endings, and a '#!/usr/bin/env python3\r' shebang makes the guest kernel search for a
        // literal interpreter named "python3\r", failing with exit 127 (observed, issue #321).
        var signalScriptBase64 = Convert.ToBase64String(
            System.Text.Encoding.ASCII.GetBytes(KvpSignalScript.Replace("\r\n", "\n")));
        var signalUnitBase64 = Convert.ToBase64String(
            System.Text.Encoding.ASCII.GetBytes(KvpSignalUnit.Replace("\r\n", "\n")));

        var script = $@"
$ErrorActionPreference = 'Stop'
$staging = '{escapedStagingRoot}'
New-Item -ItemType Directory -Force -Path $staging | Out-Null

$hash = '{escapedPasswordHash}'

$userData = @""
#cloud-config
autoinstall:
  version: 1
  identity:
    hostname: {escapedHostname}
    username: {escapedUsername}
    password: ""$hash""
  locale: {escapedLocale}
  ssh:
    install-server: true
  packages:
    - linux-cloud-tools-virtual
    - linux-tools-virtual
  late-commands:
    - curtin in-target --target=/target -- bash -c ""echo {signalScriptBase64} | base64 -d > /usr/local/sbin/hyperv-mcp-signal-ready && chmod 0755 /usr/local/sbin/hyperv-mcp-signal-ready""
    - curtin in-target --target=/target -- bash -c ""echo {signalUnitBase64} | base64 -d > /etc/systemd/system/hyperv-mcp-signal-ready.service && systemctl enable hyperv-mcp-signal-ready.service""
""@

$metaData = ""instance-id: {escapedHostname}
local-hostname: {escapedHostname}
""

Set-Content -LiteralPath (Join-Path $staging 'user-data') -Value $userData -NoNewline -Encoding ASCII
Set-Content -LiteralPath (Join-Path $staging 'meta-data') -Value $metaData -NoNewline -Encoding ASCII
Set-Content -LiteralPath (Join-Path $staging 'vendor-data') -Value '' -NoNewline -Encoding ASCII
Write-Output 'STAGE_OK'
";

        // allowDump:false as defense-in-depth even though the script carries only the hash: the
        // seed build stays out of the script-dump diagnostic entirely (SD-D4).
        var stagingResult = await _psExecutor
            .ExecuteAsync(script, timeoutSeconds: 120, ct: ct, allowDump: false)
            .ConfigureAwait(false);

        if (!stagingResult.Success)
        {
            var stagingCause = !string.IsNullOrWhiteSpace(stagingResult.Stderr)
                ? stagingResult.Stderr
                : stagingResult.Stdout;
            ThrowSeedMediaFailure(request, stagingCause, route: null);
        }

        // The route must resolve in C# before any script is built, so the imapi2 branch can be
        // forced without stubbing the PowerShell boundary.
        // See myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md — media-authoring
        // route selection.
        var authoring = await _seedMediaAuthor
            .AuthorAsync(stagingRoot, SeedVolumeLabel, seedIsoPath, ct)
            .ConfigureAwait(false);

        // Route is host-side observability only; it MUST NOT reach the caller envelope.
        _logger.LogDebug(
            "Ubuntu seed media authoring for VM '{VmName}' used route '{Route}' (success={Success}).",
            request.Name, SeedMediaAuthor.RouteToken(authoring.Route), authoring.Success);

        if (!authoring.Success)
        {
            ThrowSeedMediaFailure(request, authoring.Cause, SeedMediaAuthor.RouteToken(authoring.Route));
        }
    }

    /// <summary>
    /// Order is capture → sanitize → truncate → surface. The step is always named; the cause is
    /// omitted only when the step genuinely reported none, and the message says so rather than
    /// going silently blank.
    /// See myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md — ISO-D37 / ISO-D38.
    /// </summary>
    private void ThrowSeedMediaFailure(UbuntuInstallRequest request, string? rawCause, string? route)
    {
        var (cause, truncated) = ErrorMapper.PrepareDiagnosticCause(rawCause, request.AdminPassword);

        _logger.LogDebug(
            "Ubuntu seed media build failed for VM '{VmName}' (route={Route}); cause present={HasCause}.",
            request.Name, route ?? "staging", cause is not null);

        throw new LinuxProvisionFailedException(
            ComposeFailureMessage(
                "Failed to build the Ubuntu cloud-init seed media.", StepSeedMediaBuild, cause),
            failingStep: StepSeedMediaBuild,
            vmName: request.Name,
            cause: cause,
            causeTruncated: truncated,
            authoringRoute: route);
    }

    /// <summary>
    /// Names the failing step and embeds the sanitized cause, so a reader who sees only
    /// <c>message</c> is not left with a bare fixed string.
    /// </summary>
    private static string ComposeFailureMessage(string summary, string failingStep, string? cause)
        => string.IsNullOrEmpty(cause)
            ? $"{summary} Failing step: '{failingStep}'. The step reported no underlying cause."
            : $"{summary} Failing step: '{failingStep}'. Cause: {cause}";

    /// <summary>
    /// Creates the Gen-2 / Secure-Boot-off VM (no vTPM), attaches the Ubuntu ISO on the primary DVD
    /// and the seed ISO on the secondary DVD, and starts it DVD-first.
    /// Caller-supplied prerequisite: the primary ISO must ALREADY carry the literal
    /// <c>autoinstall</c> kernel token (see scripts/prepare-ubuntu-autoinstall-iso.py). The
    /// <c>CIDATA</c> seed supplies the answers but does not bypass subiquity's consent prompt, so a
    /// stock Ubuntu 24.04 ISO stalls at "Continue with autoinstall?" no matter how correct the seed
    /// is. This server does not remaster the ISO at runtime.
    /// See myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md — ISO-D25 / ISO-D29.
    /// </summary>
    private async Task<string?> CreateVmAndAttachMediaAsync(
        UbuntuInstallRequest request, string seedIsoPath, CancellationToken ct)
    {
        var hostProfile = _hostResolver.ResolveRequired(request.HostId);
        var storageRoot = Environment.GetEnvironmentVariable("HYPERV_MCP_STORAGE_ROOT")
            ?? hostProfile.StorageRoot
            ?? DefaultStorageRoot;
        var resolvedSwitch = request.SwitchName
            ?? Environment.GetEnvironmentVariable("HYPERV_MCP_DEFAULT_SWITCH")
            ?? hostProfile.DefaultSwitch
            ?? "Default Switch";

        var escapedName = InputValidation.EscapePowerShellString(request.Name);
        var escapedIsoPath = InputValidation.EscapePowerShellString(request.IsoPath);
        var escapedSeedIsoPath = InputValidation.EscapePowerShellString(seedIsoPath);
        var escapedStorageRoot = InputValidation.EscapePowerShellString(storageRoot);
        var escapedSwitch = InputValidation.EscapePowerShellString(resolvedSwitch);
        var memoryBytes = request.MemoryMB * 1024L * 1024L;
        var diskBytes = (long)request.DiskSizeGB * 1024L * 1024L * 1024L;

        // Gen-2 has no floppy, so the NoCloud seed rides a second DVD.
        // See myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md — ISO-D25 / ISO-D29.
        //
        // The pre-create Get-VM probe emits VM_EXISTS so a duplicate name maps to the early
        // name-collision contract (VM_ALREADY_EXISTS), not the generic LINUX_PROVISION_FAILED —
        // mirrors vm_create (Issue #203).
        //
        // Every host-side cmdlet below MUST carry -ComputerName localhost; without it they throw
        // "Value cannot be null. Parameter name: name" (Issue #357). -ErrorAction SilentlyContinue
        // is no exemption — Enable-VMIntegrationService silently swallowed that failure, leaving
        // KVP (the sole Ubuntu completion signal) disabled on every run.
        // See myplans/vm-management/lifecycle/lifecycle-design.md — LF-D7, and
        // myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md — ISO-D45.
        var script = $@"
$ErrorActionPreference = 'Stop'
$name = '{escapedName}'
$existing = Get-VM -Name $name -ComputerName localhost -ErrorAction SilentlyContinue
if ($existing) {{ Write-Output 'VM_EXISTS'; return }}
$vmDir = Join-Path '{escapedStorageRoot}' $name
New-Item -ItemType Directory -Force -Path $vmDir | Out-Null
$vhdPath = Join-Path $vmDir ($name + '.vhdx')

$vm = New-VM -Name $name -Generation 2 -MemoryStartupBytes {memoryBytes} -SwitchName '{escapedSwitch}' -Path '{escapedStorageRoot}' -NewVHDPath $vhdPath -NewVHDSizeBytes {diskBytes} -ComputerName localhost -ErrorAction Stop
Write-Output (""VM_CREATED_ID="" + $vm.Id.ToString())
try {{
    Set-VM -Name $name -Notes ""hyper-v-mcp:created=$(Get-Date -Format o);type=iso-install;role=ephemeral"" -ComputerName localhost -ErrorAction Stop
}} catch {{
    Write-Output 'VM_TAG_FAILED'
    throw
}}
Set-VMProcessor -VMName $name -Count {request.CpuCount} -ComputerName localhost -ErrorAction Stop

# Secure Boot OFF; no vTPM is enabled (unlike the Windows path).
Set-VMFirmware -VMName $name -EnableSecureBoot Off -ComputerName localhost -ErrorAction Stop

# Primary DVD = Ubuntu ISO; secondary DVD = NoCloud CIDATA seed.
Add-VMDvdDrive -VMName $name -Path '{escapedIsoPath}' -ComputerName localhost -ErrorAction Stop
Add-VMDvdDrive -VMName $name -Path '{escapedSeedIsoPath}' -ComputerName localhost -ErrorAction Stop

# DVD-first boot for install (the Ubuntu install DVD).
$dvd = Get-VMDvdDrive -VMName $name -ComputerName localhost | Where-Object {{ $_.Path -eq '{escapedIsoPath}' }} | Select-Object -First 1
if ($dvd) {{ Set-VMFirmware -VMName $name -FirstBootDevice $dvd -ComputerName localhost -ErrorAction Stop }}

# Enable guest data-exchange (KVP) so the host can read the completion key.
Enable-VMIntegrationService -VMName $name -Name 'Key-Value Pair Exchange' -ComputerName localhost -ErrorAction SilentlyContinue

Start-VM -Name $name -ComputerName localhost -ErrorAction Stop
$vm = Get-VM -Name $name -ComputerName localhost
[PSCustomObject]@{{ vmId = $vm.Id.ToString(); state = $vm.State.ToString() }} | ConvertTo-Json -Compress
";

        var result = await _psExecutor.ExecuteAsync(script, timeoutSeconds: 300, ct: ct).ConfigureAwait(false);

        var stdout = result.Stdout ?? string.Empty;
        var stderr = result.Stderr ?? string.Empty;

        var outputLines = stdout.Split('\n').Select(line => line.Trim()).ToArray();
        var tagWriteFailed = outputLines.Contains("VM_TAG_FAILED");
        const string createdIdPrefix = "VM_CREATED_ID=";
        var createdIdLine = outputLines.FirstOrDefault(line => line.StartsWith(createdIdPrefix, StringComparison.Ordinal));
        var createdVmId = createdIdLine is not null && Guid.TryParse(createdIdLine[createdIdPrefix.Length..], out var parsedVmId)
            ? parsedVmId.ToString()
            : null;

        // Duplicate VM name → the early name-collision contract (VM_ALREADY_EXISTS, Issue #203),
        // not LINUX_PROVISION_FAILED. Detected from the pre-create VM_EXISTS sentinel or a New-VM
        // "already exists" race. Message format is pinned by VmAlreadyExistsException (VC-DUP-D5)
        // so smoke tests can assert string equality.
        if (createdVmId is null && !tagWriteFailed && (stdout.Contains("VM_EXISTS", StringComparison.Ordinal) ||
            IsNameCollisionSignal(stdout) || IsNameCollisionSignal(stderr)))
        {
            throw new VmAlreadyExistsException(hostProfile.HostId, request.Name);
        }

        if (!result.Success || tagWriteFailed)
        {
            _logger.LogDebug(
                "Ubuntu VM creation failed for '{VmName}': timedOut={TimedOut} exit={ExitCode} stderr={Stderr}",
                request.Name, result.TimedOut, result.ExitCode, result.Stderr);
            // Pre-completion failure: no VM guaranteed — surface as provisioning-failed with a
            // sanitized message. The seed is still cleaned by the caller's finally.
            var (createCause, createCauseTruncated) = ErrorMapper.PrepareDiagnosticCause(
                string.IsNullOrWhiteSpace(stderr) ? stdout : stderr, request.AdminPassword);
            var summary = tagWriteFailed
                ? $"Failed to record ownership for Ubuntu VM '{request.Name}'; " +
                    "the VM is preserved, untagged and absent from the managed list."
                : "Failed to create or start the Ubuntu VM.";
            throw new LinuxProvisionFailedException(
                ComposeFailureMessage(summary, StepVmCreate, createCause),
                failingStep: StepVmCreate,
                vmId: createdVmId,
                vmName: request.Name,
                cause: createCause,
                causeTruncated: createCauseTruncated);
        }
        return createdVmId;
    }

    /// <summary>
    /// True when <paramref name="text"/> carries Hyper-V's canonical name-collision signal:
    /// "already exists" co-occurring with a VM-specific token. Mirrors the vm_create
    /// classifier (Issue #203) so a duplicate name maps to VM_ALREADY_EXISTS, not a generic
    /// provisioning failure. Guards against the BASE_IMAGE_MUTATED false-positive.
    /// </summary>
    private static bool IsNameCollisionSignal(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (text.Contains("BASE_IMAGE_MUTATED", StringComparison.Ordinal))
            return false;
        if (!text.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            return false;
        return text.Contains("VM with name", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Get-VM", StringComparison.OrdinalIgnoreCase)
            || text.Contains("New-VM", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ISO-D26 / FR-7: polls the guest completion KVP until it reports <c>ready</c> (success) or
    /// <c>failed:&lt;shortcode&gt;</c> (provisioning failure), or the <c>timeoutMinutes</c> deadline
    /// elapses. Success REQUIRES a real <c>ready</c> KVP: a silent/pending KVP never resolves to
    /// success here — the deadline path returns <c>Pending</c>, which the caller maps to
    /// <c>LINUX_INSTALL_TIMEOUT</c> with the VM preserved (FR-11).
    ///
    /// WHY no heartbeat→success: "guest is up" is not "install finished". Per
    /// myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md — ISO-D26, the only
    /// completion authority is the guest-originated KVP; a heartbeat/uptime heuristic must never
    /// stand in for it, or a mid-install reboot would be mis-reported as success.
    /// </summary>
    private async Task<GuestCompletionStatus> WaitForCompletionAsync(
        UbuntuInstallRequest request, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(request.TimeoutMinutes);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var status = await _kvpReader.ReadCompletionAsync(request.Name, ct).ConfigureAwait(false);
            if (status.Signal == GuestCompletionSignal.Ready || status.Signal == GuestCompletionSignal.Failed)
            {
                return status;
            }

            try
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        // Deadline with neither ready nor failed observed → timeout (caller preserves the VM).
        return new GuestCompletionStatus(GuestCompletionSignal.Pending, null);
    }

    /// <summary>
    /// Reads the VM id/state and the PRIMARY DVD drive's media path for preserve-VM diagnostics;
    /// best-effort (nulls on failure). The DVD path is read from the VM rather than echoed from the
    /// request, because triage needs the media actually attached.
    /// See myplans/vm-management/iso-installation/ubuntu-media-capability-and-timeout-diagnostics-design.md
    /// — UMD-D7.
    /// </summary>
    private async Task<(string? VmId, string? State, string? AttachedIsoPath)> TryGetVmIdentityAsync(
        UbuntuInstallRequest request, CancellationToken ct)
    {
        var escapedName = InputValidation.EscapePowerShellString(request.Name);
        var script = $@"
$ErrorActionPreference = 'Stop'
try {{
    $vm = Get-VM -Name '{escapedName}' -ComputerName localhost -ErrorAction Stop
    $dvdPath = $null
    try {{
        $dvdPath = (Get-VMDvdDrive -VMName '{escapedName}' -ComputerName localhost -ErrorAction Stop | Select-Object -First 1).Path
    }} catch {{ $dvdPath = $null }}
    [PSCustomObject]@{{ vmId = $vm.Id.ToString(); state = $vm.State.ToString(); attachedIsoPath = $dvdPath }} | ConvertTo-Json -Compress
}} catch {{ Write-Output '{{}}' }}
";
        try
        {
            var result = await _psExecutor.ExecuteAsync(script, timeoutSeconds: 30, ct).ConfigureAwait(false);
            var trimmed = (result.Stdout ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(trimmed)) return (null, null, null);
            using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
            var vmId = doc.RootElement.TryGetProperty("vmId", out var idProp) ? idProp.GetString() : null;
            var state = doc.RootElement.TryGetProperty("state", out var stProp) ? stProp.GetString() : null;
            var attachedIsoPath =
                doc.RootElement.TryGetProperty("attachedIsoPath", out var dvdProp)
                    && dvdProp.ValueKind == System.Text.Json.JsonValueKind.String
                    ? dvdProp.GetString()
                    : null;
            return (vmId, state, string.IsNullOrWhiteSpace(attachedIsoPath) ? null : attachedIsoPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read VM identity for '{VmName}'.", request.Name);
            return (null, null, null);
        }
    }

    /// <summary>Best-effort channel probe; any failure leaves the state Undetermined, never healthy.</summary>
    private async Task<GuestCompletionChannelState> ProbeChannelStateAsync(
        UbuntuInstallRequest request, CancellationToken ct)
    {
        try
        {
            return await _kvpReader.ProbeChannelStateAsync(request.Name, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not probe the completion channel for '{VmName}'.", request.Name);
            return GuestCompletionChannelState.Undetermined;
        }
    }

    /// <summary>
    /// Composes the timeout message from the summary (which passes through the redactor) and the two
    /// mandated triage items, which are appended AFTERWARDS and never fed to it. That ordering — not
    /// a redactor exemption list — is what stops any future sanitization change from silently
    /// stripping them; neither item can carry a secret, one being a file path and the other a closed
    /// enum.
    ///
    /// <paramref name="rawSummary"/> and <paramref name="password"/> are parameters rather than
    /// inlined text so the redacted region is genuinely variable: the two properties this method
    /// carries — a secret in the summary is redacted, and neither mandated item is — are only
    /// falsifiable if a caller can supply credential-bearing input.
    /// See myplans/vm-management/iso-installation/ubuntu-media-capability-and-timeout-diagnostics-design.md
    /// — UMD-D8.
    /// </summary>
    internal static string ComposeTimeoutMessage(
        int timeoutMinutes,
        string? attachedIsoPath,
        GuestCompletionChannelState channelState,
        string? rawSummary = null,
        string? password = null)
    {
        var (summary, _) = ErrorMapper.PrepareDiagnosticCause(
            rawSummary ??
                $"Ubuntu autoinstall did not complete within {timeoutMinutes} minutes; " +
                "the VM has been preserved for inspection.",
            password);

        return (summary ?? $"Ubuntu autoinstall did not complete within {timeoutMinutes} minutes.")
            + $" Attached installation media: {DescribeAttachedIso(attachedIsoPath)}."
            + $" Guest completion channel: {DescribeChannelState(channelState)}.";
    }

    /// <summary>An unreadable path is reported as undetermined — never replaced by the requested one.</summary>
    internal static string DescribeAttachedIso(string? attachedIsoPath)
        => string.IsNullOrWhiteSpace(attachedIsoPath) ? "undetermined" : $"'{attachedIsoPath}'";

    internal static string DescribeChannelState(GuestCompletionChannelState channelState) => channelState switch
    {
        GuestCompletionChannelState.AvailableNoSignal => "available, but no completion signal arrived",
        GuestCompletionChannelState.Unavailable => "unavailable",
        _ => "undetermined",
    };

    /// <summary>
    /// Success finalize: eject the install DVDs (so the VM boots from disk) and return the
    /// populated <see cref="OsInstallResult"/> (FR-14). Boot-from-disk reuses firmware ordering.
    /// </summary>
    private async Task<OsInstallResult> FinalizeAndDescribeAsync(UbuntuInstallRequest request, CancellationToken ct)
    {
        var escapedName = InputValidation.EscapePowerShellString(request.Name);
        var script = $@"
$ErrorActionPreference = 'Stop'
$name = '{escapedName}'
# Remove install DVDs so subsequent boots come from disk (disk-first after install, ISO-D29).
Get-VMDvdDrive -VMName $name -ComputerName localhost -ErrorAction SilentlyContinue | Remove-VMDvdDrive -ErrorAction SilentlyContinue
$vm = Get-VM -Name $name -ComputerName localhost -ErrorAction Stop
$ip = $null
try {{
    $ip = (Get-VMNetworkAdapter -VMName $name -ComputerName localhost -ErrorAction SilentlyContinue | Select-Object -ExpandProperty IPAddresses -ErrorAction SilentlyContinue | Where-Object {{ $_ -match '^\d+\.\d+\.\d+\.\d+$' }} | Select-Object -First 1)
}} catch {{ $ip = $null }}
[PSCustomObject]@{{
    vmId = $vm.Id.ToString()
    name = $vm.Name
    state = $vm.State.ToString()
    processorCount = [int]$vm.ProcessorCount
    memoryMB = [long]($vm.MemoryStartup / 1MB)
    guestIpAddress = $ip
}} | ConvertTo-Json -Compress
";
        var result = await _psExecutor.ExecuteAsync(script, timeoutSeconds: 120, ct: ct).ConfigureAwait(false);
        var trimmed = (result.Stdout ?? string.Empty).Trim();

        var installResult = new OsInstallResult { Name = request.Name };
        if (!string.IsNullOrEmpty(trimmed))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                installResult.VmId = root.TryGetProperty("vmId", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty;
                installResult.Name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? request.Name : request.Name;
                installResult.State = root.TryGetProperty("state", out var stProp) ? stProp.GetString() ?? string.Empty : string.Empty;
                installResult.ProcessorCount = root.TryGetProperty("processorCount", out var pcProp) ? pcProp.GetInt32() : request.CpuCount;
                installResult.MemoryMB = root.TryGetProperty("memoryMB", out var memProp) ? memProp.GetInt64() : request.MemoryMB;
                installResult.GuestIpAddress = root.TryGetProperty("guestIpAddress", out var ipProp) && ipProp.ValueKind == System.Text.Json.JsonValueKind.String
                    ? ipProp.GetString()
                    : null;
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogDebug(ex, "Could not parse Ubuntu finalize result for '{VmName}'.", request.Name);
            }
        }
        return installResult;
    }

    private void TryDeleteStaging(string stagingRoot)
    {
        try
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
        catch (Exception ex)
        {
            // Best-effort: a lingering hashed-credential seed is a bounded risk; never mask the
            // primary outcome. Surface at Warning so operators can clean up if needed.
            _logger.LogWarning(ex, "Failed to delete Ubuntu seed staging directory '{StagingRoot}'.", stagingRoot);
        }
    }
}
