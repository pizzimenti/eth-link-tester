namespace EthLinkTester.Core.Preflight;

/// <summary>Detects the machine's Npcap installation.</summary>
/// <remarks>
/// An interface so the app can be developed and tested with the driver absent, present, or
/// misconfigured without touching the machine - the misconfigured cases are the ones worth
/// getting right and the hardest to produce on demand.
/// </remarks>
public interface INpcapProbe
{
    Task<NpcapStatus> DetectAsync(CancellationToken cancellationToken = default);
}
