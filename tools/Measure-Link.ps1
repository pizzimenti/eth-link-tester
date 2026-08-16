<#
.SYNOPSIS
    Runs the engine bracketed by both NICs' hardware counters, so every reported figure can be
    checked against what the hardware actually did.

.DESCRIPTION
    The engine counts a frame as sent when the driver accepts it. That is the only thing a
    userspace sender can count, and it is not the same as a frame reaching copper - a link that
    goes down mid-run made this rig report 11,336 Mbps on a gigabit cable, because a driver with no
    link accepts frames at memory speed and discards them.

    That defect was caught because 11,336 is absurd. The same gap at 10% is not absurd, looks
    exactly like cable loss, and is the reason this script exists. Bracketing a run with hardware
    counters splits the engine's single "sent" figure into three:

        accepted   what the driver took from the engine        (engine tx_frames)
        sent       what the NIC says it actually transmitted   (hardware)
        received   what the far NIC says actually arrived      (hardware)

    accepted > sent is the driver swallowing frames. sent > received is loss on the wire, which is
    the only one of the three that is about the cable at all.

.PARAMETER TransmitAdapter
    Interface alias of the sending NIC. Default: Ethernet.

.PARAMETER ReceiveAdapter
    Interface alias of the receiving NIC. Default: Ethernet 2.

.PARAMETER FrameBytes
    Wire frame size including FCS: 64 to 1518. Default 1518.

.PARAMETER Seconds
    Run length. Default 10.

.PARAMETER LinkMegabits
    Link rate the engine measures against. Default 1000.

.EXAMPLE
    .\tools\Measure-Link.ps1 -FrameBytes 64
    .\tools\Measure-Link.ps1 -TransmitAdapter 'Ethernet 2' -ReceiveAdapter 'Ethernet' -FrameBytes 64
#>
[CmdletBinding()]
param(
    [string] $TransmitAdapter = 'Ethernet',
    [string] $ReceiveAdapter  = 'Ethernet 2',
    [ValidateRange(64, 1518)]
    [int]    $FrameBytes      = 1518,
    [int]    $Seconds         = 10,
    [int]    $LinkMegabits    = 1000
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AdapterCounters.ps1')

$tx = Get-RigAdapter $TransmitAdapter
$rx = Get-RigAdapter $ReceiveAdapter

if ($tx.InterfaceGuid -eq $rx.InterfaceGuid) {
    throw 'Transmit and receive name the same adapter; no frame would cross a cable.'
}

$enginerun = Join-Path $PSScriptRoot '..\engine\target\release\enginerun.exe'
if (-not (Test-Path $enginerun)) {
    throw 'enginerun.exe not built. Run: cargo build --release  (in engine\, with the Npcap SDK on LIB)'
}

# The engine takes the pcap buffer length, which excludes the FCS the NIC appends.
$bufferBytes = $FrameBytes - 4

Write-Host ''
Write-Host "  TX  $($tx.Name)  $($tx.InterfaceDescription)  $($tx.LinkSpeed)"
Write-Host "  RX  $($rx.Name)  $($rx.InterfaceDescription)  $($rx.LinkSpeed)"
Write-Host "      $FrameBytes byte frames, ${Seconds}s"
Write-Host ''

$beforeTx = Get-AdapterSnapshot $tx.Name
$beforeRx = Get-AdapterSnapshot $rx.Name

$output = & $enginerun `
    $tx.InterfaceGuid $rx.InterfaceGuid $tx.MacAddress $rx.MacAddress `
    $bufferBytes $Seconds $LinkMegabits 2>&1

Wait-CounterSettle

$deltaTx = Get-AdapterDelta -Before $beforeTx -After (Get-AdapterSnapshot $tx.Name)
$deltaRx = Get-AdapterDelta -Before $beforeRx -After (Get-AdapterSnapshot $rx.Name)

$output | ForEach-Object { Write-Host $_ }

if ($deltaTx.SpansReset -or $deltaRx.SpansReset) {
    Write-Host ''
    Write-Host 'A NIC reset its counters during the run. Every figure below would be meaningless.'

    # Naming the adapter and the field matters more than it looks. A reset means the driver
    # reloaded or the device re-enumerated mid-run, and *which* adapter did it is the difference
    # between "the sender could not keep up" and "the receiver fell over" - opposite conclusions
    # from the same collapsed throughput.
    foreach ($delta in @($deltaTx, $deltaRx)) {
        if (-not $delta.SpansReset) { continue }
        $backwards = @('Sent', 'Received', 'SentBytes', 'ReceivedBytes',
                       'RxErrors', 'TxErrors', 'RxDiscards', 'TxDiscards') |
            Where-Object { $delta.$_ -lt 0 } |
            ForEach-Object { '{0} ({1:N0})' -f $_, $delta.$_ }
        Write-Host ('  {0} went backwards on: {1}' -f $delta.Alias, ($backwards -join ', '))
    }

    exit 2
}

# Parsed back out of the engine's own report rather than recomputed, so the comparison is against
# the number the engine actually published.
$accepted = [int64](($output | Select-String -Pattern '^tx frames\s+:\s+(\d+)').Matches.Groups[1].Value)
$engineRx = [int64](($output | Select-String -Pattern '^rx frames\s+:\s+(\d+)').Matches.Groups[1].Value)

$hwSent     = $deltaTx.Sent
$hwReceived = $deltaRx.Received

function Show-Row([string] $Label, [int64] $Value, [string] $Note = '') {
    Write-Host ('  {0,-38} {1,12:N0}   {2}' -f $Label, $Value, $Note)
}

Write-Host ''
Write-Host 'where the frames went'
Write-Host ''
Show-Row 'engine says accepted by the driver' $accepted
Show-Row "$($tx.Name) hardware says sent"      $hwSent
Show-Row "$($rx.Name) hardware says received"  $hwReceived
Show-Row 'engine says received'                $engineRx
Write-Host ''

$swallowed = $accepted - $hwSent
$wireLoss  = $hwSent - $hwReceived

# Percentages of accepted, because that is the figure the engine publishes and therefore the one a
# reader would otherwise take at face value.
$swallowedPct = if ($accepted -gt 0) { 100.0 * $swallowed / $accepted } else { 0 }
$wireLossPct  = if ($hwSent  -gt 0)  { 100.0 * $wireLoss  / $hwSent }   else { 0 }

Show-Row 'accepted but never transmitted' $swallowed ('{0:N2}% - the driver, not the cable' -f $swallowedPct)
Show-Row 'transmitted but never arrived'  $wireLoss  ('{0:N2}% - the wire' -f $wireLossPct)
Write-Host ''
Show-Row 'receive errors'   $deltaRx.RxErrors
Show-Row 'receive discards' $deltaRx.RxDiscards
Show-Row 'transmit errors'  $deltaTx.TxErrors

# True throughput from the hardware's own byte count, which no software boundary can inflate.
if ($Seconds -gt 0) {
    $hwMbps = $deltaRx.ReceivedBytes * 8 / 1e6 / $Seconds
    Write-Host ''
    Write-Host ('  {0,-38} {1,12:N1} Mbps  (far NIC hardware bytes)' -f 'delivered throughput', $hwMbps)
}

Write-Host ''
