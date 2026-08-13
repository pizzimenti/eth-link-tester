using System.Text.RegularExpressions;

namespace EthLinkTester.Core.Adapters;

/// <summary>
/// Shortens a driver's description into something worth putting on a card.
/// </summary>
/// <remarks>
/// <para>
/// Windows names connections "Ethernet" and "Ethernet 2", which carry no information at all on a
/// two-NIC rig - and a two-NIC rig is the only configuration this app is for. The hardware name is
/// what tells the user which port is which, but drivers pad it with boilerplate: "Killer E2400
/// Gigabit Ethernet Controller" is four useful characters of model number wrapped in twenty-eight
/// of noise.
/// </para>
/// <para>
/// This is heuristic string handling over vendor text that follows no standard, so it is built to
/// fail safely: anything unrecognised falls back to the full description rather than being
/// mangled. A slightly long nickname is a cosmetic problem; a wrong one sends someone to unplug
/// the wrong cable.
/// </para>
/// <para>
/// Two rules follow from that and are worth stating because both were got wrong first time.
/// Phrases are matched on <b>word boundaries</b>, or "PCI Expressway" becomes "way". And a speed
/// is <b>never</b> treated as boilerplate: "10Gbit" looks like padding next to "Network Adapter",
/// but it is the only thing distinguishing a 10G Aquantia from the 5G one beside it.
/// </para>
/// </remarks>
public static partial class AdapterNickname
{
    /// <summary>
    /// Boilerplate to remove, longest first so "Gigabit Ethernet Controller" is taken before
    /// "Controller" can strand the words around it.
    /// </summary>
    /// <remarks>
    /// Deliberately contains no speed or bus token. Those identify hardware: "Gigabit Ethernet
    /// Controller" is safe to drop as a unit because the whole phrase is generic, but "10Gbit" on
    /// its own is not.
    /// </remarks>
    private static readonly string[] Noise =
    [
        "Gigabit Ethernet Controller",
        "Gigabit Network Connection",
        "Fast Ethernet Adapter",
        "Ethernet Controller",
        "Ethernet Connection",
        "Network Connection",
        "Ethernet Adapter",
        "Network Adapter",
        "Family Controller",
        "PCI Express",
        "Controller",
        "Adapter",
    ];

    /// <summary>Trademark marks, which never help identify hardware.</summary>
    private static readonly string[] Marks = ["(R)", "(TM)", "(C)", "®", "™"];

    /// <summary>
    /// Words that only make sense joined to what follows them. Left behind when the phrase they
    /// introduced is removed, they read as a truncation: "ASIX AX88772C USB2.0 to".
    /// </summary>
    private static readonly string[] Connectives = ["to", "for", "and", "with", "-", "–"];

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex RunsOfSpace { get; }

    /// <summary>
    /// A short name for the hardware, or the description unchanged when nothing recognisable
    /// survives.
    /// </summary>
    /// <param name="description">The driver's interface description.</param>
    /// <param name="fallback">
    /// Used only when there is no description at all - normally the Windows connection name, so
    /// the card is never left blank.
    /// </param>
    public static string From(string? description, string? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return fallback ?? string.Empty;
        }

        var working = description;

        foreach (var mark in Marks)
        {
            working = working.Replace(mark, " ", StringComparison.OrdinalIgnoreCase);
        }

        working = KeepWhatItIsNotWhatItConvertsTo(working);

        foreach (var phrase in Noise)
        {
            working = RemoveWholeWords(working, phrase);
        }

        working = RunsOfSpace.Replace(working, " ").Trim();
        working = DropStrandedConnectives(working);

        // Nothing but boilerplate: the description was all noise, so keep the original rather
        // than showing a fragment or a blank.
        return working.Length == 0 ? description : working;
    }

    /// <summary>
    /// Truncates at a standalone "to", which separates what a device is from what it converts to.
    /// </summary>
    /// <remarks>
    /// USB adapters are named for the conversion they perform - "ASIX AX88179 USB 3.0 to Gigabit
    /// Ethernet Adapter". Everything before the "to" identifies the hardware, including the bus,
    /// which is itself distinguishing: USB 3.0 and USB 2.0 ASIX adapters are different parts. What
    /// follows is a capability description shared by every adapter of that class, so it names
    /// nothing.
    /// </remarks>
    private static string KeepWhatItIsNotWhatItConvertsTo(string value)
    {
        var match = Regex.Match(
            value, @"\bto\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Only when something survives in front of it - a description opening with "to" is not
        // one of these, and truncating would leave nothing.
        return match.Success && value[..match.Index].Trim().Length > 0
            ? value[..match.Index]
            : value;
    }

    /// <summary>
    /// Removes a phrase only where it stands as whole words.
    /// </summary>
    /// <remarks>
    /// A plain substring search cut "PCI Express" out of "PCI Expressway" and left "way", and
    /// turned "NetworkAdapter Pro" into "Network Pro". Both are worse than leaving the description
    /// alone, because they look like real names.
    /// </remarks>
    private static string RemoveWholeWords(string value, string phrase)
    {
        var pattern = @"\b" + Regex.Escape(phrase).Replace(@"\ ", @"\s+", StringComparison.Ordinal) + @"\b";

        return Regex.Replace(
            value, pattern, " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Drops connectives that no longer join anything, wherever they ended up.
    /// </summary>
    /// <remarks>
    /// Trailing-only trimming was not enough. Removing a phrase from the <em>middle</em> - which
    /// happens whenever a driver spells a bus differently from the noise list, as the real ASIX
    /// string "USB2.0 to Fast Ethernet Adapter" does - strands the connective mid-name.
    /// </remarks>
    private static string DropStrandedConnectives(string value)
    {
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(words.Length);

        for (var i = 0; i < words.Length; i++)
        {
            var isConnective = Connectives.Contains(words[i], StringComparer.OrdinalIgnoreCase);

            // A connective earns its place only between two surviving words.
            if (isConnective && (i == words.Length - 1 || kept.Count == 0))
            {
                continue;
            }

            kept.Add(words[i]);
        }

        // Removing a trailing connective can expose another behind it.
        while (kept.Count > 0 && Connectives.Contains(kept[^1], StringComparer.OrdinalIgnoreCase))
        {
            kept.RemoveAt(kept.Count - 1);
        }

        return string.Join(' ', kept);
    }
}
