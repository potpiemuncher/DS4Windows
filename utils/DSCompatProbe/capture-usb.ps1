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
#   .\capture-usb.ps1 -Seconds 8 -InjectDescriptors -RestartDualSense
# RestartDualSense briefly restarts only USB\VID_054C&PID_0CE6 so a trace can
# include enumeration traffic. It is not required by freezeusb, which can read
# the wire HID descriptor directly through the parent hub.
#Requires -RunAsAdministrator
param(
    [int]$Seconds = 180,
    [switch]$InjectDescriptors,
    [switch]$RestartDualSense,
    [string]$OutDir = "$PSScriptRoot\usb_captures\$(Get-Date -Format yyyyMMdd_HHmmss)"
)

Start-Transcript -Path "$PSScriptRoot\capture-usb.log" -Force | Out-Null

$cmd = "C:\Program Files\USBPcap\USBPcapCMD.exe"
if (-not (Test-Path $cmd)) { Write-Error "USBPcap not installed"; Stop-Transcript; exit 1 }

# USBPcapCMD's output doesn't survive PowerShell's native-exe pipe capture,
# but cmd-level file redirection works reliably — enumerate via temp file.
$listFile = Join-Path $env:TEMP "usbpcap_ifaces.txt"
cmd /c "`"$cmd`" --extcap-interfaces > `"$listFile`" 2>&1" | Out-Null
$ifaceOutput = Get-Content $listFile -ErrorAction SilentlyContinue
$ifaces = $ifaceOutput | Select-String -Pattern "value=(\\\\\.\\USBPcap\d+)" -AllMatches |
    ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value }
if (-not $ifaces) {
    Write-Error "No USBPcap capture interfaces found. Reboot after installing USBPcap, then retry."
    Stop-Transcript
    exit 1
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
Write-Host "Capturing on $($ifaces.Count) root hub(s) for $Seconds seconds -> $OutDir"
Write-Host "PLAY NOW: fire weapons, melee, sail storms - trigger + haptic moments." -ForegroundColor Yellow

$procs = @()
foreach ($iface in $ifaces) {
    $n = $iface -replace ".*USBPcap", ""
    $captureArgs = @("-d", $iface, "-o", "$OutDir\hub$n.pcap", "-A")
    if ($InjectDescriptors) { $captureArgs += "--inject-descriptors" }
    $procs += Start-Process -FilePath $cmd -PassThru -WindowStyle Hidden -ArgumentList $captureArgs
}

if ($RestartDualSense) {
    Start-Sleep -Seconds 2
    $controller = Get-PnpDevice -PresentOnly | Where-Object {
        $_.InstanceId -like 'USB\VID_054C&PID_0CE6\*'
    } | Select-Object -First 1
    if (-not $controller) {
        Write-Error "No wired USB DualSense (VID 054C, PID 0CE6) found to restart"
    } else {
        Write-Host "Restarting $($controller.InstanceId) to capture real enumeration descriptors..."
        & pnputil.exe /restart-device $controller.InstanceId
    }
}

Start-Sleep -Seconds $Seconds
$procs | Where-Object { -not $_.HasExited } | Stop-Process -Force
Write-Host "Capture complete. Files:"
Get-ChildItem $OutDir | Format-Table Name, Length
Write-Host "Decode hint: tshark -r hubN.pcap -Y 'usb.idVendor == 0x054c' (find the hub with DualSense traffic)"
Stop-Transcript
