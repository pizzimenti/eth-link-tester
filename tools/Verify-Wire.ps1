<#
.SYNOPSIS
    Proves that frames injected through Npcap cross physical copper, using the NICs' own counters.

.DESCRIPTION
    This is the reproducible form of the central claim in README.md. Everything else in the
    repository measures frames in userspace at both ends, and userspace cannot answer the question
    that matters: a software bridge forwards a frame with its destination MAC unchanged, so the
    frame a bridge hands back is byte-identical to one that crossed a cable. `pcap_setdirection`
    would separate them and Npcap does not implement it.

    The NIC's own counters can answer it, because they count what the hardware did. This script
    brackets a wirecheck run with a snapshot of both adapters and asserts four things:

      1. the transmitting adapter's hardware says it sent at least Count unicast frames
      2. the receiving adapter's hardware says it received at least Count unicast frames
      3. nothing meaningful came back on the transmitting adapter  - no loop, no bridge
      4. the receiving adapter sent nothing meaningful             - the traffic was one-way

    Claims 3 and 4 are the ones that had never been checked by anything in the repository. They
    were established by hand once and written into the README as though a tool had done it.

.PARAMETER TransmitAdapter
    Interface alias of the sending NIC, as shown by Get-NetAdapter. Default: Ethernet.

.PARAMETER ReceiveAdapter
    Interface alias of the receiving NIC. Default: Ethernet 2.

.PARAMETER Count
    Frames to send. Default 1000, which is the figure the README quotes.

.PARAMETER NoiseAllowance
    Frames in the reverse direction to tolerate before calling it a loop. Windows emits ARP, LLMNR
    and mDNS on any adapter it considers up, so a hard zero would fail on a healthy rig for
    reasons that have nothing to do with the cable. Default 20.

.EXAMPLE
    .\tools\Verify-Wire.ps1
    .\tools\Verify-Wire.ps1 -TransmitAdapter 'Ethernet 2' -ReceiveAdapter 'Ethernet' -Count 5000
#>
[CmdletBinding()]
param(
    [string] $TransmitAdapter = 'Ethernet',
    [string] $ReceiveAdapter  = 'Ethernet 2',
    # At least one frame. A zero-frame run satisfies every assertion below without sending
    # anything, so the tool that exists to prove the premise would report the premise proven.
    [ValidateRange(1, [int]::MaxValue)]
    [int]    $Count           = 1000,
    [ValidateRange(0, [int]::MaxValue)]
    [int]    $NoiseAllowance  = 20
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AdapterCounters.ps1')

$tx = Get-RigAdapter $TransmitAdapter
$rx = Get-RigAdapter $ReceiveAdapter

if ($tx.InterfaceGuid -eq $rx.InterfaceGuid) {
    throw 'Transmit and receive name the same adapter; no frame would cross a cable.'
}

$wirecheck = Join-Path $PSScriptRoot '..\engine\target\release\wirecheck.exe' -Resolve -ErrorAction SilentlyContinue
if (-not $wirecheck) {
    throw 'wirecheck.exe not built. Run: cargo build --release  (in engine\, with the Npcap SDK on LIB)'
}

Write-Host ''
Write-Host "  TX  $($tx.Name)  $($tx.InterfaceDescription)"
Write-Host "      $($tx.MacAddress)  $($tx.LinkSpeed)"
Write-Host "  RX  $($rx.Name)  $($rx.InterfaceDescription)"
Write-Host "      $($rx.MacAddress)  $($rx.LinkSpeed)"

$beforeTx = Get-AdapterSnapshot $tx.Name
$beforeRx = Get-AdapterSnapshot $rx.Name

& $wirecheck $tx.InterfaceGuid $rx.InterfaceGuid $tx.MacAddress $rx.MacAddress $Count
$wirecheckExit = $LASTEXITCODE

Wait-CounterSettle

$deltaTx = Get-AdapterDelta -Before $beforeTx -After (Get-AdapterSnapshot $tx.Name)
$deltaRx = Get-AdapterDelta -Before $beforeRx -After (Get-AdapterSnapshot $rx.Name)

if ($deltaTx.SpansReset -or $deltaRx.SpansReset) {
    Write-Host ''
    Write-Host 'A NIC reset its counters during the run, so nothing below would mean anything.'
    exit 2
}

$txSent     = $deltaTx.Sent
$txReceived = $deltaTx.Received
$rxSent     = $deltaRx.Sent
$rxReceived = $deltaRx.Received
$rxErrors   = $deltaRx.RxErrors
$txErrors   = $deltaTx.TxErrors

Write-Host ''
Write-Host 'hardware counter deltas (unicast frames)'
Write-Host ''
Write-Host ('  {0,-34} {1,10}' -f "$($tx.Name) sent",     $txSent)
Write-Host ('  {0,-34} {1,10}' -f "$($rx.Name) received", $rxReceived)
Write-Host ('  {0,-34} {1,10}   (want <= {2})' -f "$($tx.Name) received", $txReceived, $NoiseAllowance)
Write-Host ('  {0,-34} {1,10}   (want <= {2})' -f "$($rx.Name) sent",     $rxSent,     $NoiseAllowance)
Write-Host ''
Write-Host ('  {0,-34} {1,10}' -f 'receive errors',  $rxErrors)
Write-Host ('  {0,-34} {1,10}' -f 'transmit errors', $txErrors)

$checks = [ordered]@{
    "wirecheck saw all $Count frames arrive"          = ($wirecheckExit -eq 0)
    "transmitting NIC's hardware sent >= $Count"      = ($txSent -ge $Count)
    "receiving NIC's hardware received >= $Count"     = ($rxReceived -ge $Count)
    'nothing came back on the transmitting NIC'       = ($txReceived -le $NoiseAllowance)
    'the receiving NIC sent nothing back'             = ($rxSent -le $NoiseAllowance)
    'no receive errors'                               = ($rxErrors -eq 0)
}

Write-Host ''
foreach ($check in $checks.GetEnumerator()) {
    Write-Host ('  [{0}] {1}' -f $(if ($check.Value) { 'ok  ' } else { 'FAIL' }), $check.Key)
}

$passed = -not ($checks.Values -contains $false)

Write-Host ''
if ($passed) {
    Write-Host 'VERDICT : FRAMES CROSSED THE WIRE'
    Write-Host '          Confirmed on both NICs own hardware counters, one-way, no loop.'
} else {
    Write-Host 'VERDICT : NOT PROVEN - see the failed checks above'
}
Write-Host ''

exit [int](-not $passed)
