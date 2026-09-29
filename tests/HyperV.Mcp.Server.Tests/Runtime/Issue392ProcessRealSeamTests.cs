using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using HyperV.Mcp.Server.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace HyperV.Mcp.Server.Tests.Runtime;

internal static class Issue392ProcessHarness
{
    public static async Task Main(string[] arguments)
    {
        var startup = new StartupInitialization();
        var directory = arguments[1];
        var variant = arguments[0];
        await Program.RunAsync(Array.Empty<string>(), startup, services =>
        {
            services.AddSingleton<IPowerShellHost>(provider => new BlockedHost(
                provider.GetRequiredService<ILogger<PowerShellHost>>(), startup,
                provider.GetRequiredService<ShutdownDeadline>(), directory, variant));
            services.AddSingleton<IHostedService>(provider => new ShutdownObserver(
                provider.GetRequiredService<IHostApplicationLifetime>(),
                provider.GetRequiredService<IPowerShellHost>(), directory, variant));
        });
    }

    internal static void WriteEvidence(string directory, string name, object value)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path + ".writing", JsonSerializer.Serialize(value));
        File.Move(path + ".writing", path, overwrite: true);
    }

    private sealed class BlockedHost : PowerShellHost
    {
        private readonly string _directory;
        private readonly string _variant;
        private readonly StartupInitialization _startup;
        private int _openCount;

        internal BlockedHost(ILogger<PowerShellHost> logger, StartupInitialization startup,
            ShutdownDeadline shutdown, string directory, string variant) : base(logger, startup, shutdown)
        {
            _directory = directory;
            _variant = variant;
            _startup = startup;
            ForceWindowsPowerShell51ForTesting = true;
        }

        protected override void OpenWindowsPowerShell51Runspace(Runspace runspace)
        {
            var inFlight = 1;
            var stateLock = new object();
            runspace.StateChanged += (_, arguments) =>
            {
                lock (stateLock)
                {
                    var state = arguments.RunspaceStateInfo.State;
                    File.AppendAllText(Path.Combine(_directory, "runspace-states.jsonl"),
                        JsonSerializer.Serialize(new { State = state.ToString(), InFlight = Volatile.Read(ref inFlight) != 0 }) + "\n");
                }
            };
            WriteEvidence(_directory, "prestart.json", GetChildIdentity());
            File.WriteAllText(Path.Combine(_directory, "seam"), Interlocked.Increment(ref _openCount).ToString());
            if (_variant == "race") WaitForFile("release-start", TimeSpan.FromSeconds(20));
            File.WriteAllText(Path.Combine(_directory, "start-released"), "true");
            _ = Task.Run(() => ObserveChildStart());
            var worker = Thread.CurrentThread;
            if (_variant == "late") _ = Task.Run(() => ObserveLateCompletion(worker));
            try
            {
                // The hold must not depend on the real remoting handshake completing.
                runspace.OpenAsync();
                File.WriteAllText(Path.Combine(_directory, "open-held"), "true");
                var deadline = Stopwatch.StartNew();
                while (deadline.Elapsed < TimeSpan.FromSeconds(135))
                {
                    var terminal = _startup.Terminal;
                    if (terminal is not null && !File.Exists(Path.Combine(_directory, "terminal.json")))
                        WriteEvidence(_directory, "terminal.json", terminal);
                    if (File.Exists(Path.Combine(_directory, "complete-open")))
                    {
                        // Production may dispose on return, so the real asynchronous open must be finished too.
                        if (runspace.RunspaceStateInfo.State == RunspaceState.Opened) return;
                    }
                    Thread.Sleep(10);
                }
                throw new TimeoutException("Harness open hold expired");
            }
            finally
            {
                lock (stateLock) Volatile.Write(ref inFlight, 0);
                File.WriteAllText(Path.Combine(_directory, "open-returned"), "true");
            }
        }

        private void ObserveChildStart()
        {
            try
            {
                var deadline = Stopwatch.StartNew();
                while (deadline.Elapsed < TimeSpan.FromSeconds(15))
                {
                    var identity = GetChildIdentity();
                    if (identity.ProcessId is not null)
                    {
                        WriteEvidence(_directory, "child.json", identity);
                        return;
                    }
                    Thread.Sleep(1);
                }
                throw new TimeoutException("The real registered remoting child never started");
            }
            catch (Exception exception) { WriteEvidence(_directory, "child-observer-error.json", exception.ToString()); }
        }

        private async Task ObserveLateCompletion(Thread worker)
        {
            try
            {
                WaitForFile("check-held", TimeSpan.FromSeconds(125));
                WriteEvidence(_directory, "held-check.json", await CheckRejectedInitialization());
                WaitForFile("complete-open", TimeSpan.FromSeconds(20));
                if (!worker.Join(TimeSpan.FromSeconds(20)))
                    throw new TimeoutException("The production initialization thread did not finish after release");
                WriteEvidence(_directory, "late-check.json", await CheckRejectedInitialization());
            }
            catch (Exception exception) { WriteEvidence(_directory, "late-observer-error.json", exception.ToString()); }
        }

        private async Task<object> CheckRejectedInitialization()
        {
            var failures = new List<string>();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await EnsureInitializedAsync().WaitAsync(TimeSpan.FromSeconds(1));
                    failures.Add("unexpected success");
                }
                catch (InvalidOperationException exception) { failures.Add(exception.Message); }
            }
            return new { Terminal = _startup.Terminal, Diagnostics = GetInitDiagnostics(), OpenCount = _openCount, Failures = failures };
        }

        private void WaitForFile(string name, TimeSpan budget)
        {
            var elapsed = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(_directory, name)))
            {
                if (elapsed.Elapsed >= budget) throw new TimeoutException($"Harness did not receive {name}");
                Thread.Sleep(10);
            }
        }
    }

    private sealed class ShutdownObserver : IHostedService, IDisposable
    {
        private readonly string _directory;
        private readonly string _variant;
        private readonly CancellationTokenRegistration _registration;

        private readonly IPowerShellHost _host;

        internal ShutdownObserver(IHostApplicationLifetime lifetime, IPowerShellHost host, string directory, string variant)
        {
            _directory = directory;
            _variant = variant;
            _host = host;
            _registration = lifetime.ApplicationStopping.Register(() =>
                WriteEvidence(directory, "shutdown-started.json", new
                {
                    Identity = host.GetInitDiagnostics().ChildIdentity,
                    StartReleased = File.Exists(Path.Combine(directory, "start-released")),
                    InputCloseRequested = File.Exists(Path.Combine(directory, "input-close-requested.json")),
                    Stack = Environment.StackTrace
                }));
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
            if (_variant == "forced")
            {
                File.WriteAllText(Path.Combine(_directory, "forced-disposal-held"), "true");
                Thread.Sleep(TimeSpan.FromSeconds(15));
            }
            if (_variant == "race")
            {
                WriteEvidence(_directory, "shutdown-paused.json", new
                {
                    Identity = _host.GetInitDiagnostics().ChildIdentity,
                    StartReleased = File.Exists(Path.Combine(_directory, "start-released"))
                });
                var elapsed = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(_directory, "child-observed")) && elapsed.Elapsed < TimeSpan.FromSeconds(3))
                    Thread.Sleep(1);
                WriteEvidence(_directory, "race-handoff.json", new
                {
                    ChildObserved = File.Exists(Path.Combine(_directory, "child-observed")),
                    Identity = _host.GetInitDiagnostics().ChildIdentity
                });
            }
            _registration.Dispose();
        }
    }
}

[Trait("Category", "Runtime")]
[Trait("Category", "RequiresHyperV")]
public class Issue392ProcessRealSeamTests
{
    internal const string PublishedServerVariable = "ISSUE392_PUBLISHED_SERVER_EXE";

    /// <summary>Absent published-server pointers must skip, not break default tests or silently pass real-stdio coverage.
    /// Do not guess paths: -o and RID move publish output and could select the wrong binary.</summary>
    internal static string RequirePublishedServer()
    {
        var executable = Environment.GetEnvironmentVariable(PublishedServerVariable);
        if (string.IsNullOrWhiteSpace(executable))
            throw SkipBecause($"{PublishedServerVariable} is not set, so the real published-server cases cannot run. " +
                $"Publish the server and point the variable at the produced HyperV.Mcp.Server.exe, for example: " +
                $"dotnet publish src\\HyperV.Mcp.Server\\HyperV.Mcp.Server.csproj -c Debug -o <fresh-dir> " +
                $"then set {PublishedServerVariable}=<fresh-dir>\\HyperV.Mcp.Server.exe");
        if (!Path.IsPathFullyQualified(executable))
            throw SkipBecause($"{PublishedServerVariable} must be a fully-qualified path; got '{executable}'.");
        if (!File.Exists(executable))
            throw SkipBecause($"{PublishedServerVariable} points at '{executable}', which does not exist. " +
                "Re-publish the server and update the variable.");
        AssertNotStale(executable);
        return executable;
    }

    /// <summary>A pinned build older than tracked source invalidates A2 coverage; fail on stale mtime rather than warn.</summary>
    private static void AssertNotStale(string executable)
    {
        var repository = FindRepositoryRoot();
        if (repository is null) return;
        var sources = Directory.EnumerateFiles(Path.Combine(repository, "src"), "*.cs", SearchOption.AllDirectories);
        var newest = sources.Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();
        if (newest is null) return;
        var built = File.GetLastWriteTimeUtc(executable);
        if (built < newest.LastWriteTimeUtc)
            throw SkipBecause($"the binary pinned by {PublishedServerVariable} ('{executable}', built {built:O}) " +
                $"predates '{newest.FullName}' (modified {newest.LastWriteTimeUtc:O}); re-publish before trusting this run");
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                && Directory.Exists(Path.Combine(directory.FullName, "src"))) return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    private static Exception SkipBecause(string reason) => new InvalidOperationException(reason);

    /// <summary>xUnit v2 requires discovery-time skips, so check unusable published-server pointers here, not in the test body.</summary>
    internal sealed class PublishedServerTheoryAttribute : TheoryAttribute
    {
        public override string? Skip
        {
            get
            {
                try { RequirePublishedServer(); return null; }
                catch (Exception exception) { return exception.Message; }
            }
            set => throw new NotSupportedException();
        }
    }

    private readonly ITestOutputHelper _output;
    public Issue392ProcessRealSeamTests(ITestOutputHelper output) => _output = output;

    [PublishedServerTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AppendixA2_StrippedEnvironmentReadiness(bool windir, bool computerName)
    {
        await RunCase("normal", windir, computerName, async server =>
        {
            await server.Initialize();
            await server.WaitForStderr("Startup terminal: Ready");
            await server.Send(new
            {
                jsonrpc = "2.0", id = 2, method = "tools/call",
                @params = new { name = "vm_diag", arguments = new { } }
            });
            using var reply = JsonDocument.Parse(await server.Response(2));
            var result = reply.RootElement.GetProperty("result");
            if (result.TryGetProperty("isError", out var error)) error.GetBoolean().Should().BeFalse();
            var content = result.GetProperty("content").EnumerateArray()
                .Single(block => block.GetProperty("type").GetString() == "text");
            using var toolReply = JsonDocument.Parse(content.GetProperty("text").GetString()!);
            var envelope = toolReply.RootElement;
            envelope.GetProperty("success").GetBoolean().Should().BeTrue();
            var data = envelope.GetProperty("data");
            data.GetProperty("dotnet").GetProperty("machineName").GetString().Should().Be(Environment.MachineName);
            var host = data.GetProperty("phase2Host");
            host.GetProperty("startupState").GetString().Should().Be("Ready");
            host.GetProperty("initialized").GetBoolean().Should().BeTrue();
            var readinessElapsed = server.Elapsed;
            server.Record("readiness-observed", new { Elapsed = readinessElapsed, MachineName = Environment.MachineName });
            readinessElapsed.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(125));
            await server.CloseAndAssertExit(0);
            server.AssertOneTerminalReport();
        });
    }

    [Theory]
    [InlineData("clean", 0, true)]
    [InlineData("forced", 3, true)]
    [InlineData("early", 0, false)]
    public async Task AppendixA3_BlockedOpenUsesProductionLifecycle(string variant, int expectedExit, bool waitForTimeout)
    {
        await RunCase(variant, false, false, async server =>
        {
            await server.Initialize();
            using var recordedChild = await server.AssertBlockedChild();
            if (waitForTimeout) await server.AssertTimeout();
            else server.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(120));
            await server.CloseAndAssertExit(expectedExit);
            recordedChild.WaitForExit(250).Should().BeTrue("the exact child captured before shutdown must exit");
            server.Record("child-exit", new { recordedChild.Id, Exited = recordedChild.HasExited });
            await server.AssertNoDisposalDuringOpen();
            if (variant == "forced") await server.AssertSdkInitiatedBackstop();
            (await server.ReadEvidenceText("seam")).Should().Be("1");
            File.Exists(Path.Combine(server.Directory, "open-returned")).Should().BeFalse();
            if (waitForTimeout) server.AssertOneTerminalReport();
            else server.Stderr.Should().NotContain("Startup terminal:");
        });
    }

    [Fact]
    public async Task AppendixA3g_ShutdownDuringPreStartPauseHasExplicitMarker()
    {
        await RunCase("race", false, false, async server =>
        {
            await server.Initialize();
            await server.WaitForFile("seam");
            AssertNotStarted(await server.Identity("prestart.json"));
            server.CloseInput();
            foreach (var marker in new[] { "shutdown-started.json", "shutdown-paused.json" })
            {
                using var pause = await server.ReadEvidence(marker);
                pause.RootElement.GetProperty("StartReleased").GetBoolean().Should().BeFalse();
                AssertNotStarted(pause.RootElement.GetProperty("Identity").Deserialize<PowerShellChildIdentity>()!);
            }
            File.Exists(Path.Combine(server.Directory, "start-released")).Should().BeFalse();
            File.Exists(Path.Combine(server.Directory, "child.json")).Should().BeFalse();
            File.WriteAllText(Path.Combine(server.Directory, "release-start"), "true");
            await server.WaitForFile("start-released");
            using var recordedChild = server.ObserveLiveChild(await server.Identity("child.json"));
            File.WriteAllText(Path.Combine(server.Directory, "child-observed"), "true");
            await server.AssertExit(0);
            using var handoff = await server.ReadEvidence("race-handoff.json");
            handoff.RootElement.GetProperty("ChildObserved").GetBoolean().Should().BeTrue();
            var handedOffIdentity = handoff.RootElement.GetProperty("Identity").Deserialize<PowerShellChildIdentity>()!;
            handedOffIdentity.ProcessId.Should().Be(recordedChild.Id);
            handedOffIdentity.StartTimeUtc.Should().Be(recordedChild.StartTime.ToUniversalTime());
            var survivor = !recordedChild.HasExited;
            server.Record("approved-fr2-exception", new
            {
                recordedChild.Id, StartTimeUtc = recordedChild.StartTime.ToUniversalTime(), Survivor = survivor,
                Interleaving = "shutdown observed while registered child start held; real start acknowledged after release"
            });
            await server.AssertNoDisposalDuringOpen();
        });
    }

    [Fact]
    public async Task AppendixA4_LateOpenCannotReplaceTimeoutOrReprobe()
    {
        await RunCase("late", false, false, async server =>
        {
            await server.Initialize();
            using var recordedChild = await server.AssertBlockedChild();
            var terminal = await server.AssertTimeout();
            File.WriteAllText(Path.Combine(server.Directory, "check-held"), "true");
            using var held = await server.ReadEvidence("held-check.json");
            AssertRejectedInitialization(held.RootElement, terminal);
            await server.AssertNoDisposalDuringOpen();
            File.Exists(Path.Combine(server.Directory, "open-returned")).Should().BeFalse();
            recordedChild.HasExited.Should().BeFalse();
            File.WriteAllText(Path.Combine(server.Directory, "complete-open"), "true");
            using var late = await server.ReadEvidence("late-check.json");
            await server.WaitForFile("open-returned");
            AssertRejectedInitialization(late.RootElement, terminal);
            await server.AssertNoDisposalDuringOpen();
            await server.CloseAndAssertExit(0);
            recordedChild.WaitForExit(250).Should().BeTrue();
            await server.AssertNoDisposalDuringOpen();
            server.AssertOneTerminalReport();
            (await server.ReadEvidenceText("seam")).Should().Be("1");
        });
    }

    private static void AssertNotStarted(PowerShellChildIdentity identity)
    {
        identity.Status.Should().Be("child not yet started");
        identity.ProcessId.Should().BeNull();
        identity.StartTimeUtc.Should().BeNull();
    }

    private static void AssertRejectedInitialization(JsonElement check, string terminal)
    {
        check.GetProperty("Terminal").GetRawText().Should().Be(terminal);
        check.GetProperty("OpenCount").GetInt32().Should().Be(1);
        var failures = check.GetProperty("Failures").EnumerateArray().Select(value => value.GetString()).ToArray();
        failures.Should().HaveCount(2).And.OnlyContain(value => value != null && value.Contains("TimedOutStillRunning"));
        var diagnostics = check.GetProperty("Diagnostics");
        diagnostics.GetProperty("Initialized").GetBoolean().Should().BeFalse();
        diagnostics.GetProperty("StartupState").GetString().Should().Be("TimedOutStillRunning");
        using var original = JsonDocument.Parse(terminal);
        diagnostics.GetProperty("StartupDetail").GetString().Should().Be(original.RootElement.GetProperty("Detail").GetString());
    }

    private async Task RunCase(string variant, bool windir, bool computerName, Func<ServerProcess, Task> body)
    {
        using var server = new ServerProcess(variant, windir, computerName);
        _output.WriteLine($"Retained evidence: {server.Directory}");
        var failed = false;
        try
        {
            server.Start();
            // A timing failure must not prevent the independent lifecycle assertions from running.
            using (new AssertionScope())
            {
                await body(server);
                server.ValidateStdout();
            }
            server.Record("assertions", "completed");
        }
        catch (Exception exception)
        {
            failed = true;
            server.Record("failure", exception.ToString());
            throw;
        }
        finally
        {
            try { await server.Finish(); }
            catch (Exception exception) when (failed) { server.Record("cleanup-error", exception.ToString()); }
            finally
            {
                foreach (var line in (await server.Report()).Split('\n'))
                    for (var offset = 0; offset < Math.Max(1, line.Length); offset += 1024)
                        _output.WriteLine(line.Substring(offset, Math.Min(1024, line.Length - offset)));
            }
        }
    }

    private sealed class ServerProcess : IDisposable
    {
        private readonly Process _process;
        private readonly Stopwatch _elapsed = new();
        private readonly ConcurrentQueue<string> _stderr = new();
        private readonly ConcurrentQueue<string> _stdout = new();
        private readonly ConcurrentQueue<string> _captureErrors = new();
        private readonly ConcurrentQueue<(string Line, TimeSpan Elapsed)> _stderrTimes = new();
        private readonly string _variant;
        private readonly List<PowerShellChildIdentity> _children = new();
        private readonly List<string> _diagnosticErrors = new();
        private StreamWriter? _input;
        private Stopwatch? _shutdownElapsed;
        private TimeSpan? _exitElapsed;
        private bool _started;
        private int _inputCloseCount;
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "issue392-tests", Guid.NewGuid().ToString("N"));
        internal string Stderr => string.Join("\n", _stderr);
        internal TimeSpan Elapsed => _elapsed.Elapsed;

        internal ServerProcess(string variant, bool windir, bool computerName)
        {
            System.IO.Directory.CreateDirectory(Directory);
            _variant = variant;
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(typeof(Issue392ProcessHarness).Assembly.Location);
            start.ArgumentList.Add(variant);
            start.ArgumentList.Add(Directory);
            var inherited = new Dictionary<string, string?>(start.Environment, StringComparer.OrdinalIgnoreCase);
            start.Environment.Clear();
            foreach (var name in new[] { "APPDATA", "HOMEDRIVE", "HOMEPATH", "LOCALAPPDATA", "PATH",
                "PROCESSOR_ARCHITECTURE", "SYSTEMDRIVE", "SYSTEMROOT", "TEMP", "USERNAME", "USERPROFILE" })
                if (inherited.TryGetValue(name, out var value)) start.Environment[name] = value;
            if (windir) start.Environment["windir"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (computerName) start.Environment["COMPUTERNAME"] = Environment.MachineName;
            _process = new Process { StartInfo = start };
            _process.OutputDataReceived += (_, arguments) =>
            {
                if (arguments.Data is null) return;
                _stdout.Enqueue(arguments.Data);
                try { File.AppendAllText(Path.Combine(Directory, "stdout.log"), arguments.Data + "\n"); }
                catch (Exception exception) { _captureErrors.Enqueue(exception.ToString()); }
            };
            _process.ErrorDataReceived += (_, arguments) =>
            {
                if (arguments.Data is null) return;
                _stderrTimes.Enqueue((arguments.Data, _elapsed.Elapsed));
                _stderr.Enqueue(arguments.Data);
                try { File.AppendAllText(Path.Combine(Directory, "stderr.log"), arguments.Data + "\n"); }
                catch (Exception exception) { _captureErrors.Enqueue(exception.ToString()); }
            };
            Record("case", new { variant, windir, computerName });
        }

        internal void Start()
        {
            if (_variant == "normal")
            {
                var executable = RequirePublishedServer();
                _process.StartInfo.FileName = executable;
                _process.StartInfo.ArgumentList.Clear();
            }
            Record("launch", new { _process.StartInfo.FileName, Arguments = _process.StartInfo.ArgumentList.ToArray() });
            _elapsed.Start();
            _started = _process.Start();
            Assert.True(_started, "Process.Start must launch the selected executable");
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _input = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            Record("server", new { _process.Id, StartTimeUtc = _process.StartTime.ToUniversalTime() });
        }

        internal async Task Initialize()
        {
            await Send(new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "issue392", version = "1" } }
            });
            using var response = JsonDocument.Parse(await Response(1));
            response.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString().Should().NotBeNullOrWhiteSpace();
            await Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
        }

        internal Task Send(object message) => _input!.WriteLineAsync(JsonSerializer.Serialize(message));

        internal async Task<string> Response(int id)
        {
            string? result = null;
            await WaitUntil(() =>
            {
                foreach (var line in _stdout)
                {
                    using var document = JsonDocument.Parse(line);
                    if (document.RootElement.TryGetProperty("id", out var identifier) && identifier.ValueKind == JsonValueKind.Number && identifier.GetInt32() == id)
                    {
                        document.RootElement.TryGetProperty("error", out _).Should().BeFalse(line);
                        result = line;
                        return true;
                    }
                }
                return false;
            });
            return result!;
        }

        internal Task WaitForStderr(string marker) => WaitUntil(() => Stderr.Contains(marker, StringComparison.Ordinal));
        internal Task WaitForFile(string name) => WaitUntil(() => File.Exists(Path.Combine(Directory, name)));
        internal async Task<PowerShellChildIdentity> Identity(string name)
        {
            using var document = await ReadEvidence(name);
            return document.RootElement.Deserialize<PowerShellChildIdentity>()!;
        }

        internal async Task<JsonDocument> ReadEvidence(string name) =>
            JsonDocument.Parse(await ReadEvidenceText(name));

        internal async Task<string> ReadEvidenceText(string name)
        {
            string? contents = null;
            var path = Path.Combine(Directory, name);
            await WaitUntil(() =>
            {
                if (!File.Exists(path)) return false;
                contents = File.ReadAllText(path);
                return true;
            });
            return contents!;
        }

        internal Process ObserveLiveChild(PowerShellChildIdentity identity)
        {
            identity.Status.Should().Be("started");
            identity.ProcessId.Should().BeGreaterThan(0, "a PS7 success or missing real child must fail this case");
            identity.StartTimeUtc.Should().NotBeNull();
            _children.Add(identity);
            var child = Process.GetProcessById(identity.ProcessId!.Value);
            try
            {
                child.StartTime.ToUniversalTime().Should().Be(identity.StartTimeUtc);
                child.HasExited.Should().BeFalse();
                Record("observed-child", identity);
                return child;
            }
            catch { child.Dispose(); throw; }
        }

        internal async Task<Process> AssertBlockedChild()
        {
            (await ReadEvidenceText("seam")).Should().Be("1");
            AssertNotStarted(await Identity("prestart.json"));
            await WaitForFile("open-held");
            File.Exists(Path.Combine(Directory, "open-returned")).Should().BeFalse();
            return ObserveLiveChild(await Identity("child.json"));
        }

        internal async Task<string> AssertTimeout()
        {
            const string marker = "Startup terminal: TimedOutStillRunning";
            await WaitForStderr(marker);
            var report = _stderrTimes.Single(entry => entry.Line.Contains(marker, StringComparison.Ordinal));
            Record("timeout-observed", new { report.Elapsed, report.Line });
            report.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(115))
                .And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(125));
            report.Line.Should().Contain("runspace.Open").And.Contain("WindowsPowerShell51")
                .And.Contain("still running; not cancelled").And.Contain("restart");
            var child = _children.Single();
            report.Line.Should().Contain($"child=pid={child.ProcessId}; startUtc={child.StartTimeUtc:O}");
            using var terminal = await ReadEvidence("terminal.json");
            terminal.RootElement.GetProperty("State").GetString().Should().Be("TimedOutStillRunning");
            terminal.RootElement.GetProperty("Progress").GetProperty("Stage").GetString().Should().Be("runspace.Open");
            terminal.RootElement.GetProperty("Progress").GetProperty("Edition").GetString().Should().Be("WindowsPowerShell51");
            return terminal.RootElement.GetRawText();
        }

        internal void AssertOneTerminalReport()
        {
            _stderr.Count(line => line.Contains("Startup terminal:")).Should().Be(1);
            if (_variant != "normal")
                _stderr.Count(line => line.Contains("PowerShellHost probe starting.")).Should().Be(1,
                    "the full production probe sequence must not restart, even if a second attempt never reaches the PS5.1 seam");
        }

        internal async Task AssertNoDisposalDuringOpen()
        {
            string[] lines = null!;
            await WaitUntil(() =>
            {
                lines = File.ReadAllLines(Path.Combine(Directory, "runspace-states.jsonl"));
                return true;
            });
            lines.Should().NotBeEmpty("StateChanged observation must actually be active");
            foreach (var line in lines)
            {
                using var state = JsonDocument.Parse(line);
                if (!state.RootElement.GetProperty("InFlight").GetBoolean()) continue;
                state.RootElement.GetProperty("State").GetString().Should().NotBe("Closing").And.NotBe("Closed");
            }
        }

        internal void ValidateStdout()
        {
            _captureErrors.Should().BeEmpty("all redirected output must be retained");
            foreach (var line in _stdout)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                root.ValueKind.Should().Be(JsonValueKind.Object);
                root.GetProperty("jsonrpc").GetString().Should().Be("2.0");
                (root.TryGetProperty("method", out _) || root.TryGetProperty("id", out _)).Should().BeTrue(line);
            }
        }

        internal void Record(string name, object value) => Issue392ProcessHarness.WriteEvidence(Directory, name + ".json", value);

        private static bool IsSharingViolation(IOException exception) =>
            exception is not FileNotFoundException and not DirectoryNotFoundException
            && exception.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);

        private async Task WaitUntil(Func<bool> condition)
        {
            IOException? lastSharingViolation = null;
            while (true)
            {
                var evidenceReadBlocked = false;
                try
                {
                    if (condition()) return;
                    foreach (var error in new[] { "child-observer-error.json", "late-observer-error.json" })
                    {
                        var path = Path.Combine(Directory, error);
                        if (File.Exists(path)) Assert.Fail(File.ReadAllText(path));
                    }
                }
                catch (IOException exception) when (IsSharingViolation(exception))
                {
                    // Windows can transiently lock a just-renamed file, even after the server exits.
                    evidenceReadBlocked = true;
                    lastSharingViolation = exception;
                }
                if (_elapsed.Elapsed >= TimeSpan.FromSeconds(_variant == "late" ? 150 : 125)
                    || (!evidenceReadBlocked && _process.HasExited))
                    Assert.Fail($"Required seam/response was not observed. Elapsed={_elapsed.Elapsed}. STDERR:\n{Stderr}"
                        + (evidenceReadBlocked ? $"\nLast sharing violation: {lastSharingViolation}" : ""));
                await Task.Delay(10);
            }
        }

        internal void CloseInput()
        {
            if (_shutdownElapsed is not null) return;
            if (!_started) return;
            Record("input-close-requested", new { Elapsed, ShutdownAlreadyObserved = File.Exists(Path.Combine(Directory, "shutdown-started.json")) });
            _shutdownElapsed = Stopwatch.StartNew();
            _input!.Close();
            _inputCloseCount++;
            Record("input-closed", new { Count = _inputCloseCount });
        }

        internal async Task CloseAndAssertExit(int expectedExit)
        {
            CloseInput();
            await AssertExit(expectedExit);
        }

        internal async Task AssertSdkInitiatedBackstop()
        {
            _inputCloseCount.Should().Be(1);
            using var input = await ReadEvidence("input-close-requested.json");
            input.RootElement.GetProperty("ShutdownAlreadyObserved").GetBoolean().Should().BeFalse();
            using var shutdown = await ReadEvidence("shutdown-started.json");
            shutdown.RootElement.GetProperty("InputCloseRequested").GetBoolean().Should().BeTrue();
            shutdown.RootElement.GetProperty("Stack").GetString().Should().Contain("McpServerHostedService")
                .And.NotContain("StdioPeerShutdownWatchdog");
            File.Exists(Path.Combine(Directory, "forced-disposal-held")).Should().BeTrue();
            Stderr.Should().Contain("shutdown was already requested; arming shutdown backstop without another stop request")
                .And.Contain("force-exiting with code 3")
                .And.NotContain("deferring to it").And.NotContain("standing down");
            Record("sdk-backstop-route", new { InputCloseCount = _inputCloseCount, ExitCode = _process.ExitCode });
        }

        internal async Task AssertExit(int expectedExit)
        {
            var exitDeadline = Stopwatch.StartNew();
            while (!_process.HasExited && exitDeadline.Elapsed < TimeSpan.FromSeconds(6)) await Task.Delay(1);
            Assert.True(_process.HasExited, "the server must exit after stdin closes; STDERR:\n" + Stderr);
            _exitElapsed = _shutdownElapsed!.Elapsed;
            Record("exit", new { Expected = expectedExit, Actual = _process.ExitCode, ShutdownElapsed = _exitElapsed });
            // Process teardown cannot be measured to the millisecond; bound the 5s grace period with 2s tolerance.
            _exitElapsed.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(7));
            _process.ExitCode.Should().Be(expectedExit, Stderr);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            ValidateStdout();
        }

        internal async Task Finish()
        {
            if (!_started) return;
            try
            {
                CloseInput();
                if (!_process.HasExited)
                {
                    try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6)); }
                    catch (TimeoutException)
                    {
                        Record("harness-cleanup", "server still alive after assertions; killing only the launched process tree");
                        _process.Kill(entireProcessTree: true);
                        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                    }
                }
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                Record("final-process", new { _process.ExitCode, Elapsed, ShutdownElapsed = _shutdownElapsed?.Elapsed });
                ValidateStdout();
            }
            catch (Exception exception) { Record("finish-error", exception.ToString()); throw; }
            finally
            {
                var identityPath = Path.Combine(Directory, "child.json");
                if (File.Exists(identityPath))
                {
                    var contents = await ReadDiagnosticText(identityPath);
                    if (contents is not null)
                    {
                        try
                        {
                            var identity = JsonSerializer.Deserialize<PowerShellChildIdentity>(contents);
                            if (identity is not null && !_children.Contains(identity)) _children.Add(identity);
                        }
                        catch (Exception exception)
                        {
                            _diagnosticErrors.Add($"child.json: unreadable ({exception.GetType().Name}: {exception.Message})");
                        }
                    }
                }
                foreach (var identity in _children)
                {
                    try
                    {
                        using var child = Process.GetProcessById(identity.ProcessId!.Value);
                        if (child.StartTime.ToUniversalTime() != identity.StartTimeUtc || child.HasExited) continue;
                        Record("survivor-before-harness-cleanup", identity);
                        child.Kill();
                        child.WaitForExit(1000).Should().BeTrue("test cleanup must not leave the recorded child behind");
                    }
                    catch (ArgumentException) { }
                }
            }
        }

        private async Task<string?> ReadDiagnosticText(string path)
        {
            for (var attempt = 0; ; attempt++)
            {
                try { return File.ReadAllText(path); }
                catch (Exception exception)
                {
                    if (attempt < 2 && exception is IOException ioException && IsSharingViolation(ioException))
                    {
                        await Task.Delay(10);
                        continue;
                    }
                    _diagnosticErrors.Add($"{Path.GetFileName(path)}: unreadable ({exception.GetType().Name}: {exception.Message})");
                    return null;
                }
            }
        }

        internal async Task<string> Report()
        {
            var details = new StringBuilder($"Retained evidence: {Directory}\n");
            try
            {
                foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
                {
                    var contents = await ReadDiagnosticText(path);
                    if (contents is not null) details.AppendLine($"{Path.GetFileName(path)}: {contents}");
                }
            }
            catch (Exception exception)
            {
                _diagnosticErrors.Add($"Evidence directory unreadable ({exception.GetType().Name}: {exception.Message})");
            }
            foreach (var error in _diagnosticErrors) details.AppendLine(error);
            details.AppendLine($"STDERR:\n{Stderr}");
            return details.ToString();
        }

        public void Dispose() => _process.Dispose();
    }
}
