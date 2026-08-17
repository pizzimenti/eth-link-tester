using EthLinkTester.Core.Topology;

namespace EthLinkTester.Core.Tests;

/// <summary>
/// Pins the managed side of the sweep against the same fixture the engine's tests read.
/// </summary>
/// <remarks>
/// <para>
/// The sweep is defined twice, once per language: <c>engine/src/topology.rs</c> puts the frames on
/// the wire and <see cref="ReservedMulticastProbe"/> interprets what came back. Four things have to
/// stay in step across that boundary by hand - set membership, the names the two sides match each
/// other by, which single address decides the verdict, and send order - and nothing about the
/// boundary makes any of them fail to compile when they drift. The discriminator has already moved
/// once this phase, from <c>-00</c> to <c>-02</c>, so it is a thing that moves.
/// </para>
/// <para>
/// This is the same job the telemetry struct's offset assertions do for the other seam, written
/// against a shared file because this side of the boundary is a list rather than a layout. A change
/// on either side without a change to the fixture breaks that side's tests; a change to the fixture
/// breaks whichever side has not followed.
/// </para>
/// <para>
/// The long-term fix is for the engine to own the wire outright and report
/// <c>(name, decides, arrived)</c> per address across the FFI, so the managed side interprets what
/// the engine says rather than holding a parallel copy. Until that integration exists, this is what
/// keeps the copies honest.
/// </para>
/// </remarks>
public class SweepSeamTests
{
    private sealed record Row(string Name, string Mac, bool Decides);

    private static IReadOnlyList<Row> Fixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "topology-sweep.txt");

        Assert.True(File.Exists(path), $"the shared sweep fixture is missing from {path}");

        return
        [
            .. File.ReadAllLines(path)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .Select(line => line.Split(','))
                .Select(fields => new Row(fields[0], fields[1], fields[2] == "yes")),
        ];
    }

    [Fact]
    public void TheEnumHasExactlyTheAddressesTheEngineSends()
    {
        var expected = Fixture().Select(r => r.Name).ToList();
        var actual = Enum.GetNames<ProbeAddress>().ToList();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Declaration order is send order, and the control goes first.
    /// </summary>
    /// <remarks>
    /// The engine has a test named <c>the_control_is_swept_first</c>, because nothing else in the
    /// sweep means anything until the path is known to carry frames. This enum used to declare the
    /// control last, so any future managed code deriving order from <c>Enum.GetValues</c> would
    /// have inverted that invariant without a word from the compiler.
    /// </remarks>
    [Fact]
    public void TheControlIsFirstOnBothSides()
    {
        Assert.Equal("Control", Fixture()[0].Name);
        Assert.Equal(ProbeAddress.Control, Enum.GetValues<ProbeAddress>()[0]);
    }

    [Fact]
    public void EveryAddressMatchesTheOneTheEngineSendsTo()
    {
        foreach (var row in Fixture())
        {
            var address = Enum.Parse<ProbeAddress>(row.Name);

            Assert.Equal(row.Mac, address.Mac());
        }
    }

    /// <summary>
    /// Exactly one address decides, and it is the same one on both sides.
    /// </summary>
    /// <remarks>
    /// The engine carries a <c>discriminating</c> flag and its verdict follows the flag; the
    /// managed side used to hardcode <see cref="ProbeAddress.SlowProtocols"/> independently. Two
    /// expressions of one rule, in different languages, with nothing comparing them.
    /// </remarks>
    [Fact]
    public void OneAddressDecides_AndBothSidesAgreeWhich()
    {
        var deciding = Fixture().Where(r => r.Decides).ToList();

        Assert.Single(deciding);
        Assert.True(Enum.Parse<ProbeAddress>(deciding[0].Name).Decides());
        Assert.Single(Enum.GetValues<ProbeAddress>(), a => a.Decides());
    }

    /// <summary>
    /// The two addresses the sweep must never contain, asserted here as well as in the engine.
    /// </summary>
    /// <remarks>
    /// <c>-00</c> is conformantly forwarded by S-VLAN and TPMR components and default-forwarded by
    /// common Realtek silicon; <c>-01</c> is PAUSE, which essentially every receiving MAC consumes
    /// by destination address alone. Either would make the sweep report a direct cable through a
    /// switch, or a switch on a bare cable.
    /// </remarks>
    [Theory]
    [InlineData("01:80:C2:00:00:00")]
    [InlineData("01:80:C2:00:00:01")]
    public void TheSweepAvoidsTheAddressesThatCannotWork(string forbidden)
    {
        Assert.DoesNotContain(forbidden, Fixture().Select(r => r.Mac));
        Assert.DoesNotContain(forbidden, Enum.GetValues<ProbeAddress>().Select(a => a.Mac()));
    }
}
