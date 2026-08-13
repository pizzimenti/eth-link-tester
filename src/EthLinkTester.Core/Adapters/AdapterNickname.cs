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
/// </remarks>
public static partial class AdapterNickname
{
    /// <summary>
    /// Boilerplate to remove, longest first so "Gigabit Ethernet Controller" is taken before
    /// "Controller" can strand the words around it.
    /// </summary>
    private static readonly string[] Noise =
    [
        "USB 3.0 to Gigabit Ethernet",
        "USB 2.0 to Fast Ethernet",
        "Gigabit Ethernet Controller",
        "Gigabit Network Connection",
        "10Gbit Network Adapter",
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

        foreach (var phrase in Noise)
        {
            var index = working.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                working = working.Remove(index, phrase.Length);
            }
        }

        working = RunsOfSpace.Replace(working, " ").Trim();

        // Removing a phrase can leave the connective that introduced it - "ASIX AX88179 USB 3.0
        // to" - which reads as a truncation rather than a name.
        working = TrimConnectives(working);

        // Nothing but boilerplate: the description was all noise, so keep the original rather
        // than showing a fragment or a blank.
        return working.Length == 0 ? description : working;
    }

    private static string TrimConnectives(string value)
    {
        string[] connectives = ["to", "for", "and", "with", "-", "–"];

        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        while (words.Count > 0
               && connectives.Contains(words[^1], StringComparer.OrdinalIgnoreCase))
        {
            words.RemoveAt(words.Count - 1);
        }

        return string.Join(' ', words);
    }
}
