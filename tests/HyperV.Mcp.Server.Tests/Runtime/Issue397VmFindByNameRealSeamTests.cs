using System.Management.Automation.Language;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using HyperV.Mcp.Server.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

[Trait("Category", "Runtime")]
public class Issue397VmFindByNameRealSeamTests
{
    private const string Inventory = """
        [
          {"Id":"tagged","Name":"Build-VM","State":"Running","Notes":"hyper-v-mcp:role=ephemeral"},
          {"Id":"untagged","Name":"build-vm","State":"Off","Notes":""},
          {"Id":"duplicate","Name":"Build-VM","State":"Paused"},
          {"Id":"partial","Name":"Build-VM-extra","State":"Saved"}
        ]
        """;

    [Theory]
    [InlineData(null, 3)]
    [InlineData(false, 3)]
    [InlineData(true, 2)]
    [InlineData("false", 3)]
    [InlineData("true", 2)]
    public async Task Matching_ReturnsEveryWholeNameMatch_WithOnlyPublicFields(object? caseSensitive, int count)
    {
        var executor = new RecordingExecutor(new PowerShellResult { Stdout = Inventory });
        using var fixture = new Fixture(executor);
        var arguments = new Dictionary<string, object?> { ["name"] = "Build-VM" };
        if (caseSensitive is not null)
            arguments["caseSensitive"] = caseSensitive;

        using var response = JsonDocument.Parse(await fixture.Dispatcher.DispatchAsync("vm_find_by_name", arguments));
        response.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        var data = response.RootElement.GetProperty("data");
        data.GetProperty("count").GetInt32().Should().Be(count);
        var matches = data.GetProperty("matches").EnumerateArray().ToArray();
        matches.Select(row => row.GetProperty("vmId").GetString()).Should().Equal(
            count == 3 ? new[] { "tagged", "untagged", "duplicate" } : new[] { "tagged", "duplicate" });
        matches.Select(row => row.GetProperty("state").GetString()).Should().Equal(
            count == 3 ? new[] { "Running", "Off", "Paused" } : new[] { "Running", "Paused" });
        matches.Select(row => row.GetProperty("name").GetString()).Should().Equal(
            count == 3 ? new[] { "Build-VM", "build-vm", "Build-VM" } : new[] { "Build-VM", "Build-VM" });
        foreach (var match in matches)
            match.EnumerateObject().Select(property => property.Name).Should().Equal("vmId", "name", "state");
        fixture.GlobalGate.Verify(gate => gate.AcquireGlobalSlotAsync(
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Slot.Verify(slot => slot.Dispose(), Times.Once);
        fixture.GlobalGate.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("*")]
    [InlineData("?")]
    [InlineData("[")]
    [InlineData("a[b]")]
    [InlineData("quote'; $(Write-Output injected)")]
    [InlineData(" padded ")]
    public async Task Punctuation_IsLiteral_AndNeverEntersScript(string name)
    {
        var payload = JsonSerializer.Serialize(new[]
        {
            new { Id = "literal", Name = name, State = "Off" },
            new { Id = "other", Name = "unrelated", State = "Running" },
            new { Id = "partial", Name = name + "suffix", State = "Saved" },
        });
        var executor = new RecordingExecutor(new PowerShellResult { Stdout = payload });
        using var fixture = new Fixture(executor);
        foreach (var caseSensitive in new[] { false, true })
        {
            var matches = await fixture.Manager.FindVmsByNameAsync("local", name, caseSensitive);
            matches.Should().ContainSingle().Which.VmId.Should().Be("literal");
            matches[0].HostId.Should().Be("local");
        }
        var literalScript = executor.LastScript;
        await fixture.Manager.FindVmsByNameAsync("local", "different caller name", false);
        executor.LastScript.Should().Be(literalScript);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData(" \r\n [ ] \t")]
    [InlineData(Inventory)]
    public async Task NoMatches_ReturnsEmptySuccess(string output)
    {
        using var fixture = new Fixture(new RecordingExecutor(new PowerShellResult { Stdout = output }));
        using var response = JsonDocument.Parse(await fixture.Dispatch("missing"));
        response.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        response.RootElement.GetProperty("data").GetProperty("count").GetInt32().Should().Be(0);
        response.RootElement.GetProperty("data").GetProperty("matches").GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData("{}", "name")]
    [InlineData("{\"name\":null}", "name")]
    [InlineData("{\"name\":\"\"}", "name")]
    [InlineData("{\"name\":\"  \\t\"}", "name")]
    [InlineData("{\"name\":\"vm\",\"caseSensitive\":1}", "caseSensitive")]
    [InlineData("{\"name\":\"vm\",\"caseSensitive\":null}", "caseSensitive")]
    [InlineData("{\"name\":\"vm\",\"caseSensitive\":\"True\"}", "caseSensitive")]
    [InlineData("{\"name\":\"vm\",\"caseSensitive\":\" false \"}", "caseSensitive")]
    [InlineData("{\"name\":\"vm\",\"caseSensitive\":{}}", "caseSensitive")]
    public async Task InvalidInput_IsNamed_AndDoesNotInspectHost(string argumentsJson, string parameter)
    {
        var executor = new RecordingExecutor(new PowerShellResult { Stdout = "[]" });
        using var fixture = new Fixture(executor);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson)!;
        using var response = JsonDocument.Parse(await fixture.Dispatcher.DispatchAsync("vm_find_by_name", arguments));
        response.RootElement.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.InvalidParameter);
        response.RootElement.GetProperty("error").GetString().Should().Contain(parameter);
        executor.LastScript.Should().BeNull();
        fixture.GlobalGate.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("true", 2)]
    [InlineData("false", 3)]
    [InlineData("\"true\"", 2)]
    [InlineData("\"false\"", 3)]
    public async Task JsonBooleans_UseStrictHelper(string booleanJson, int count)
    {
        using var fixture = new Fixture(new RecordingExecutor(new PowerShellResult { Stdout = Inventory }));
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            "{\"name\":\"Build-VM\",\"caseSensitive\":" + booleanJson + "}")!;
        using var response = JsonDocument.Parse(await fixture.Dispatcher.DispatchAsync("vm_find_by_name", arguments));
        response.RootElement.GetProperty("data").GetProperty("count").GetInt32().Should().Be(count);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Id\":\"id\",\"Name\":\"vm\",\"State\":\"Off\"}")]
    [InlineData("[null]")]
    [InlineData("[1]")]
    [InlineData("[{}]")]
    [InlineData("[{\"Name\":\"vm\",\"State\":\"Off\"}]")]
    [InlineData("[{\"Id\":\"\",\"Name\":\"vm\",\"State\":\"Off\"}]")]
    [InlineData("[{\"Id\":5,\"Name\":\"vm\",\"State\":\"Off\"}]")]
    [InlineData("[{\"Id\":\"id\",\"State\":\"Off\"}]")]
    [InlineData("[{\"Id\":\"id\",\"Name\":\"\",\"State\":\"Off\"}]")]
    [InlineData("[{\"Id\":\"id\",\"Name\":null,\"State\":\"Off\"}]")]
    [InlineData("[{\"Id\":\"id\",\"Name\":\"unmatched\"}]")]
    [InlineData("[{\"Id\":\"id\",\"Name\":\"vm\",\"State\":null}]")]
    [InlineData("[{\"Id\":\"id\",\"Name\":\"vm\",\"State\":3}]")]
    [InlineData("[{\"Id\":\"id\",\"Name\":\"vm\",\"State\":\"Off\"},{}]")]
    public async Task MalformedOutput_IsLookupFailure_EvenForUnmatchedRows(string output)
    {
        using var fixture = new Fixture(new RecordingExecutor(new PowerShellResult { Stdout = output }));
        using var response = JsonDocument.Parse(await fixture.Dispatch());
        AssertLookupFailure(response, 0);
        response.RootElement.GetProperty("data").GetProperty("stderr").GetString().Should().BeEmpty();
    }

    [Theory]
    [InlineData("Access is denied")]
    [InlineData("cannot find path C:\\missing")]
    [InlineData("VM not found")]
    public async Task FailedEnumeration_DoesNotUseTextClassification_AndRedactsStderr(string stderr)
    {
        using var fixture = new Fixture(new RecordingExecutor(new PowerShellResult
        {
            ExitCode = 5,
            Stdout = "private stdout",
            Stderr = stderr + " password=lookup-secret",
        }));
        var json = await fixture.Dispatch();
        using var response = JsonDocument.Parse(json);
        AssertLookupFailure(response, 5);
        response.RootElement.GetProperty("data").GetProperty("stderr").GetString().Should().Contain(stderr);
        json.Should().NotContain("lookup-secret").And.NotContain("private stdout");
    }

    [Fact]
    public async Task ExecutorException_IsLookupFailure_WithoutExposingExceptionText()
    {
        var executor = new RecordingExecutor(_ => throw new UnauthorizedAccessException("private exception text"));
        using var fixture = new Fixture(executor);
        var json = await fixture.Dispatch();
        using var response = JsonDocument.Parse(json);
        AssertLookupFailure(response, null);
        json.Should().NotContain("private exception text");
    }

    [Theory]
    [InlineData("pre-cancelled")]
    [InlineData("exception")]
    [InlineData("result")]
    [InlineData("token-success")]
    [InlineData("token-failure")]
    [InlineData("token-exception")]
    public async Task Cancellation_PropagatesThroughDispatcher(string scenario)
    {
        using var cancellation = new CancellationTokenSource();
        var executor = new RecordingExecutor(token =>
        {
            if (scenario == "exception")
                throw new OperationCanceledException(token);
            if (scenario.StartsWith("token-", StringComparison.Ordinal))
                cancellation.Cancel();
            if (scenario == "token-exception")
                throw new InvalidOperationException("Access is denied");
            return new PowerShellResult
            {
                Stdout = "[]",
                ExitCode = scenario == "token-failure" ? 1 : 0,
                Cancelled = scenario == "result",
            };
        });
        using var fixture = new Fixture(executor);
        if (scenario == "pre-cancelled")
            cancellation.Cancel();
        var action = () => fixture.Dispatch(ct: cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        if (scenario != "pre-cancelled")
            fixture.Slot.Verify(slot => slot.Dispose(), Times.Once);
    }

    [Theory]
    [InlineData("unknown", ErrorCodes.HostNotFound)]
    [InlineData("remote", ErrorCodes.InvalidParameter)]
    public async Task NonLocalHost_IsRejected_WithoutInspectingLocalhost(string hostId, string errorCode)
    {
        var executor = new RecordingExecutor(new PowerShellResult { Stdout = Inventory });
        using var fixture = new Fixture(executor);
        using var response = JsonDocument.Parse(await fixture.Dispatcher.DispatchAsync("vm_find_by_name",
            new Dictionary<string, object?> { ["name"] = "Build-VM", ["hostId"] = hostId }));
        response.RootElement.GetProperty("errorCode").GetString().Should().Be(errorCode);
        executor.LastScript.Should().BeNull();
    }

    [Fact]
    public async Task Script_UsesOnlyReadOnlyCommands_AndExplicitLocalEnumeration()
    {
        var executor = new RecordingExecutor(new PowerShellResult { Stdout = "[]" });
        using var fixture = new Fixture(executor);
        await fixture.Dispatch("caller-name-not-in-script");
        var script = executor.LastScript!;
        script.Should().NotContain("caller-name-not-in-script").And.NotContain("Notes");
        script.Should().Contain("Get-VM -Name '*' -ComputerName localhost -ErrorAction Stop");
        script.Should().Contain("[string]$_.State").And.Contain("ConvertTo-Json -InputObject @(");
        foreach (var verb in new[] { "Set-", "Stop-", "Start-", "Remove-", "Checkpoint-" })
            script.Should().NotContain(verb);
        var syntax = Parser.ParseInput(script, out _, out var errors);
        errors.Should().BeEmpty();
        syntax.FindAll(node => node is CommandAst, true).Cast<CommandAst>()
            .Select(command => command.GetCommandName()).Should().BeEquivalentTo(
                "Import-Module", "Get-VM", "Select-Object", "ConvertTo-Json");
    }

    [Fact]
    public async Task McpBinding_AndCatalog_ExposeLookup()
    {
        var definition = ToolCatalog.AllTools.Single(tool => tool.Name == "vm_find_by_name");
        definition.Category.Should().Be(ToolCategory.Discovery);
        definition.Priority.Should().Be(ToolPriority.P1);
        var method = typeof(VmTools).GetMethod(nameof(VmTools.VmFindByName))!;
        method.GetCustomAttribute<McpServerToolAttribute>()!.Name.Should().Be("vm_find_by_name");
        method.GetParameters().Single(parameter => parameter.Name == "name").IsOptional.Should().BeFalse();
        method.GetParameters().Single(parameter => parameter.Name == "caseSensitive").DefaultValue.Should().Be(false);
        using var fixture = new Fixture(new RecordingExecutor(new PowerShellResult { Stdout = Inventory }));
        using var response = JsonDocument.Parse(await VmTools.VmFindByName(fixture.Dispatcher, "Build-VM"));
        response.RootElement.GetProperty("data").GetProperty("count").GetInt32().Should().Be(3);
    }

    [Theory]
    [Trait("Category", "RealPowerShell")]
    [InlineData(0, null)]
    [InlineData(1, null)]
    [InlineData(3, null)]
    [InlineData(0, "Access is denied")]
    [InlineData(0, "cannot find path")]
    public async Task RealExecutor_EnforcesArrayAndStringState_AndInspectionFailure(int count, string? error)
    {
        var executor = new StubHostExecutor(count, error);
        using var fixture = new Fixture(executor);
        using var response = JsonDocument.Parse(await fixture.Dispatch("Build-VM"));
        if (error is not null)
        {
            executor.LastResult!.ExitCode.Should().NotBe(0);
            AssertLookupFailure(response, executor.LastResult.ExitCode);
            response.RootElement.GetProperty("data").GetProperty("stderr").GetString().Should().Contain(error);
            return;
        }
        response.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        response.RootElement.GetProperty("data").GetProperty("count").GetInt32().Should().Be(count);
        using var output = JsonDocument.Parse(executor.LastResult!.Stdout);
        output.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        output.RootElement.GetArrayLength().Should().Be(count);
        foreach (var row in output.RootElement.EnumerateArray())
            row.GetProperty("State").GetString().Should().Be("Paused");
    }

    private static void AssertLookupFailure(JsonDocument response, int? exitCode)
    {
        var root = response.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.CommandFailed);
        root.GetProperty("error").GetString().Should().Be(VmLookupFailedException.SafeMessage);
        var data = root.GetProperty("data");
        data.EnumerateObject().Select(property => property.Name).Should().Equal("exitCode", "stderr");
        if (exitCode is null)
            data.GetProperty("exitCode").ValueKind.Should().Be(JsonValueKind.Null);
        else
            data.GetProperty("exitCode").GetInt32().Should().Be(exitCode.Value);
    }

    private sealed class RecordingExecutor : IPowerShellExecutor
    {
        private readonly Func<CancellationToken, PowerShellResult> _execute;
        public string? LastScript { get; private set; }

        public RecordingExecutor(PowerShellResult result) : this(_ => result) { }
        public RecordingExecutor(Func<CancellationToken, PowerShellResult> execute) => _execute = execute;

        public Task<PowerShellResult> ExecuteAsync(
            string script, int timeoutSeconds = 300, CancellationToken ct = default, bool allowDump = true)
        {
            LastScript = script;
            return Task.FromResult(_execute(ct));
        }
    }

    private sealed class StubHostExecutor(int count, string? error) : IPowerShellExecutor
    {
        public PowerShellResult? LastResult { get; private set; }

        public async Task<PowerShellResult> ExecuteAsync(
            string script, int timeoutSeconds = 300, CancellationToken ct = default, bool allowDump = true)
        {
            // Stub only host cmdlets to preserve the production projection and JSON boundary.
            var prelude = $$"""
                enum TestVmState { Paused = 9 }
                function Import-Module { [CmdletBinding()] param($Name) }
                function Get-VM {
                    [CmdletBinding()] param($Name, $ComputerName)
                    if ($Name -ne '*' -or $ComputerName -ne 'localhost' -or $ErrorActionPreference -ne 'Stop') {
                        throw 'Unexpected enumeration scope'
                    }
                    if ('{{error}}') { Write-Error '{{error}}'; return }
                    for ($number = 0; $number -lt {{count}}; $number++) {
                        [pscustomobject]@{
                            Id = "vm-$number"
                            Name = 'Build-VM'
                            State = [TestVmState]::Paused
                            Notes = ''
                        }
                    }
                }
                """;
            var executor = new PowerShellExecutor(NullLogger<PowerShellExecutor>.Instance);
            LastResult = await executor.ExecuteAsync(prelude + Environment.NewLine + script, timeoutSeconds, ct, allowDump);
            return LastResult;
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Mock<IConcurrencyGate> GlobalGate { get; } = new(MockBehavior.Strict);
        public Mock<IDisposable> Slot { get; } = new();
        public HyperVManager Manager { get; }
        public ToolDispatcher Dispatcher { get; }

        public Fixture(IPowerShellExecutor executor)
        {
            var options = new ServerOptions { DefaultHostId = "local" };
            options.Hosts["local"] = new HostProfile { HostId = "local", ComputerName = "localhost" };
            options.Hosts["remote"] = new HostProfile { HostId = "remote", ComputerName = "other-host" };
            var resolver = new HostResolver(options);
            GlobalGate.Setup(gate => gate.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Slot.Object);
            Manager = new HyperVManager(executor, resolver, options,
                NullLogger<HyperVManager>.Instance, new TestIsoInspector());
            Dispatcher = new ToolDispatcher(Manager, Mock.Of<ICommandExecutor>(), Mock.Of<IFileTransferService>(),
                Mock.Of<ICheckpointManager>(), resolver, new ErrorMapper(), GlobalGate.Object, executor,
                Mock.Of<IPowerShellDirectChannel>(), options);
        }

        public Task<string> Dispatch(string name = "vm", CancellationToken ct = default) =>
            Dispatcher.DispatchAsync("vm_find_by_name", new Dictionary<string, object?> { ["name"] = name }, ct);

        public void Dispose() { }
    }
}
