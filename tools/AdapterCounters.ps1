<#
    Hardware counter helpers, shared by the verification scripts in this directory.

    These read what the NIC itself did, which is the only instrument on this rig that can see past
    the driver's accept boundary. Every figure the engine reports is counted either at the moment
    the driver accepted a frame or at the moment userspace dequeued one, and the gap between those
    boundaries and the copper is where this project's three worst measurement defects have all
    lived. Dot-source this rather than copying the queries: two definitions of "frames sent" is two
    chances to pick a different counter and not notice.
#>

# Unicast only, on purpose. Engine frames are unicast, while the broadcast and multicast counters
# carry the background chatter Windows generates on any live adapter - folding those in lets noise
# satisfy an assertion about our own traffic.
function Get-AdapterSnapshot {
    param([Parameter(Mandatory)][string] $Alias)

    $s = Get-NetAdapterStatistics -Name $Alias
    [pscustomobject]@{
        Alias         = $Alias
        Sent          = [int64] $s.SentUnicastPackets
        Received      = [int64] $s.ReceivedUnicastPackets
        SentBytes     = [int64] $s.SentBytes
        ReceivedBytes = [int64] $s.ReceivedBytes
        RxErrors      = [int64] $s.ReceivedPacketErrors
        TxErrors      = [int64] $s.OutboundPacketErrors
        RxDiscards    = [int64] $s.ReceivedDiscardedPackets
        TxDiscards    = [int64] $s.OutboundDiscardedPackets
    }
}

<#
    Difference between two snapshots of the same adapter.

    A negative field means the NIC's counters reset mid-interval - a disable/enable, a driver
    reload, or a USB adapter's surprise-removal - which makes every figure here meaningless rather
    than merely wrong. The caller is told so it can discard the sample instead of reporting it.
#>
function Get-AdapterDelta {
    param(
        [Parameter(Mandatory)] $Before,
        [Parameter(Mandatory)] $After
    )

    if ($Before.Alias -ne $After.Alias) {
        throw "Snapshots are from different adapters ($($Before.Alias) and $($After.Alias))."
    }

    $delta = [pscustomobject]@{
        Alias         = $After.Alias
        Sent          = $After.Sent          - $Before.Sent
        Received      = $After.Received      - $Before.Received
        SentBytes     = $After.SentBytes     - $Before.SentBytes
        ReceivedBytes = $After.ReceivedBytes - $Before.ReceivedBytes
        RxErrors      = $After.RxErrors      - $Before.RxErrors
        TxErrors      = $After.TxErrors      - $Before.TxErrors
        RxDiscards    = $After.RxDiscards    - $Before.RxDiscards
        TxDiscards    = $After.TxDiscards    - $Before.TxDiscards
        SpansReset    = $false
    }

    $delta.SpansReset = @(
        $delta.Sent, $delta.Received, $delta.SentBytes, $delta.ReceivedBytes,
        $delta.RxErrors, $delta.TxErrors, $delta.RxDiscards, $delta.TxDiscards
    ).Where({ $_ -lt 0 }).Count -gt 0

    $delta
}

<#
    Resolves an interface alias to an adapter, refusing anything that is not usable for a run.

    An adapter that is not Up produces counters that look perfectly plausible and describe nothing,
    which is the failure mode this whole toolchain exists to avoid.
#>
function Get-RigAdapter {
    param([Parameter(Mandatory)][string] $Alias)

    $adapter = Get-NetAdapter -Name $Alias -ErrorAction SilentlyContinue
    if (-not $adapter) { throw "No adapter named '$Alias'. Get-NetAdapter lists what is present." }
    if ($adapter.Status -ne 'Up') { throw "Adapter '$Alias' is $($adapter.Status), not Up." }
    $adapter
}

<#
    The counters lag the wire; without a settle the sent delta is routinely short of what was sent.
#>
function Wait-CounterSettle {
    Start-Sleep -Milliseconds 500
}
