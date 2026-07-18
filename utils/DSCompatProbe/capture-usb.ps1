# USB payload capture for the M0 compatibility lab (see doc/M0_COMPAT_LAB_RUNBOOK.md).
# Captures raw USB traffic (HID output reports incl. trigger effects, and the
# isochronous haptic/audio stream) from a wired DualSense while a native title runs.
#
# Requirements: USBPcap installed AND the machine rebooted since install.
# Must run elevated. Wireshark/tshark used afterwards to decode.
#
# Usage (elevated PowerShell):
#   .\capture-usb.ps1              # 180-second capture on all USB root hubs
#   .\capture-usb.ps1 -Seconds 300
#Requires -RunAsAdministrator
param(
    [int]$Seconds = 180,
    [string]$OutDir = "$PSScriptRoot\usb_captures\$(Get-Date -Format yyyyMMdd_HHmmss)"
)

$cmd = "C:\Program Files\USBPcap\USBPcapCMD.exe"
if (-not (Test-Path $cmd)) { Write-Error "USBPcap not installed"; exit 1 }

$ifaceOutput = & $cmd --extcap-interfaces
$ifaces = $ifaceOutput | Select-String -Pattern "value=(\\\\\.\\USBPcap\d+)" -AllMatches |
    ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value }
if (-not $ifaces) {
    Write-Error "No USBPcap capture interfaces found. Reboot after installing USBPcap, then retry."
    exit 1
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
Write-Host "Capturing on $($ifaces.Count) root hub(s) for $Seconds seconds -> $OutDir"
Write-Host "PLAY NOW: fire weapons, melee, sail storms - trigger + haptic moments." -ForegroundColor Yellow

$procs = @()
foreach ($iface in $ifaces) {
    $n = $iface -replace ".*USBPcap", ""
    $procs += Start-Process -FilePath $cmd -PassThru -WindowStyle Hidden `
        -ArgumentList "-d", $iface, "-o", "$OutDir\hub$n.pcap", "-A"
}

Start-Sleep -Seconds $Seconds
$procs | Where-Object { -not $_.HasExited } | Stop-Process -Force
Write-Host "Capture complete. Files:"
Get-ChildItem $OutDir | Format-Table Name, Length
Write-Host "Decode hint: tshark -r hubN.pcap -Y 'usb.idVendor == 0x054c' (find the hub with DualSense traffic)"
