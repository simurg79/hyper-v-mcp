using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// PR-C: VmTools boilerplate dedup — equivalence-test invariant (design §8).
///
/// Locks in the contract that the Default Interface Method (DIM) tuple overload
/// <see cref="IToolDispatcher.DispatchAsync(string, System.Threading.CancellationToken, ValueTuple{string, object}[])"/>
/// produces the exact same <see cref="Dictionary{TKey, TValue}"/> (and therefore the
/// same envelope) as a direct call to the dictionary overload with the same
/// (key, value) pairs.
///
/// Approach (a) from the brief: capture the dictionary that the DIM forwards to the
/// concrete implementing dispatcher via a test-double <see cref="IToolDispatcher"/>.
/// </summary>
[Trait("Category", "Runtime")]
public class VmToolsTupleOverloadEquivalenceTests
{
    /// <summary>
    /// Captures the (toolName, arguments, ct) triple passed to the dictionary overload
    /// and returns a deterministic envelope string built from the inputs so equivalence
    /// can be asserted on the returned <see cref="Task{TResult}"/> as well.
    /// </summary>
    private sealed class CapturingDispatcher : IToolDispatcher
    {
        public string? LastToolName { get; private set; }
        public Dictionary<string, object?>? LastArguments { get; private set; }
        public int CallCount { get; private set; }

        public Task<string> DispatchAsync(
            string toolName,
            Dictionary<string, object?> arguments,
            CancellationToken ct = default)
        {
            CallCount++;
            LastToolName = toolName;
            LastArguments = arguments;
            // Deterministic envelope that depends only on the visible inputs
            // (tool name + sorted key=value pairs) so two semantically-equivalent
            // calls produce byte-identical strings.
            var keyVals = arguments
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value ?? "<null>"}");
            return Task.FromResult($"{toolName}|{string.Join(",", keyVals)}");
        }
    }

    // ---------------------------------------------------------------------
    // 1. Empty args case (corresponds to vm_diag in production).
    // ---------------------------------------------------------------------
    [Fact]
    public async Task TupleOverload_NoArgs_ProducesSameEnvelopeAsEmptyDictionary()
    {
        IToolDispatcher tupleDisp = new CapturingDispatcher();
        IToolDispatcher dictDisp = new CapturingDispatcher();

        var tupleEnvelope = await tupleDisp.DispatchAsync("vm_diag", CancellationToken.None);
        var dictEnvelope = await dictDisp.DispatchAsync(
            "vm_diag",
            new Dictionary<string, object?>(),
            CancellationToken.None);

        tupleEnvelope.Should().Be(dictEnvelope);
        ((CapturingDispatcher)tupleDisp).LastArguments.Should().BeEmpty();
        ((CapturingDispatcher)tupleDisp).LastArguments
            .Should().Equal(((CapturingDispatcher)dictDisp).LastArguments);
    }

    // ---------------------------------------------------------------------
    // 2. Multi-arg case — mixed string/int/bool/nullable values.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task TupleOverload_MultiArg_ProducesSameEnvelopeAsDictionary()
    {
        IToolDispatcher tupleDisp = new CapturingDispatcher();
        IToolDispatcher dictDisp = new CapturingDispatcher();

        var tupleEnvelope = await tupleDisp.DispatchAsync(
            "vm_create",
            CancellationToken.None,
            ("name", (object?)"alpha"),
            ("cpuCount", (object?)4),
            ("memoryMB", (object?)8192),
            ("autoStart", (object?)true),
            ("baseVhdxPath", (object?)null));

        var expected = new Dictionary<string, object?>
        {
            ["name"] = "alpha",
            ["cpuCount"] = 4,
            ["memoryMB"] = 8192,
            ["autoStart"] = true,
            ["baseVhdxPath"] = null,
        };
        var dictEnvelope = await dictDisp.DispatchAsync(
            "vm_create", expected, CancellationToken.None);

        tupleEnvelope.Should().Be(dictEnvelope);
        ((CapturingDispatcher)tupleDisp).LastArguments.Should().Equal(expected);
    }

    // ---------------------------------------------------------------------
    // 3. Null `args` parameter — must not throw; equivalent to empty.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task TupleOverload_NullArgsArray_TreatedAsEmpty()
    {
        IToolDispatcher tupleDisp = new CapturingDispatcher();
        IToolDispatcher emptyDisp = new CapturingDispatcher();

        var nullArgs = (ValueTuple<string, object?>[])null!;
        var nullEnvelope = await tupleDisp.DispatchAsync("vm_diag", CancellationToken.None, nullArgs);
        var emptyEnvelope = await emptyDisp.DispatchAsync("vm_diag", CancellationToken.None);

        nullEnvelope.Should().Be(emptyEnvelope);
        ((CapturingDispatcher)tupleDisp).LastArguments.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------
    // 4. Duplicate-key case — must throw ArgumentException whose message names key.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task TupleOverload_DuplicateKey_ThrowsArgumentExceptionNamingKey()
    {
        IToolDispatcher disp = new CapturingDispatcher();

        var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await disp.DispatchAsync(
                "any_tool",
                CancellationToken.None,
                ("k", (object?)1),
                ("k", (object?)2)));

        ex.Message.Should().Contain("k",
            "the DIM must surface the offending duplicate key name in the exception message.");
        ((CapturingDispatcher)disp).CallCount.Should()
            .Be(0, "ArgumentException must propagate before the dictionary overload runs.");
    }

    // ---------------------------------------------------------------------
    // 5. Key-set preservation — materialized dict matches expected literal.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task TupleOverload_PreservesKeySetWithoutSilentReordering()
    {
        var capturing = new CapturingDispatcher();
        IToolDispatcher disp = capturing;

        await disp.DispatchAsync(
            "vm_copy_file",
            CancellationToken.None,
            ("vmId", (object?)"vm-1"),
            ("sourcePath", (object?)@"C:\src\a.txt"),
            ("destPath", (object?)@"C:\dst\a.txt"),
            ("isDirectory", (object?)false));

        var expected = new Dictionary<string, object?>
        {
            ["vmId"] = "vm-1",
            ["sourcePath"] = @"C:\src\a.txt",
            ["destPath"] = @"C:\dst\a.txt",
            ["isDirectory"] = false,
        };

        // Dictionary equality is set-based for keys + element-wise value equality.
        capturing.LastArguments.Should().Equal(expected);

        // Also assert insertion order is preserved (no silent reordering).
        capturing.LastArguments!.Keys.Should().ContainInOrder(
            "vmId", "sourcePath", "destPath", "isDirectory");
    }
}
