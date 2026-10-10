# ProsimCompanion crash hunt (2026-10-10): catch the native code that corrupts the heap.
# Run ON THE SIM PC, as administrator:   powershell -ExecutionPolicy Bypass -File .\enable-page-heap.ps1
# Then restart ProsimCompanion. To undo:  powershell -ExecutionPolicy Bypass -File .\enable-page-heap.ps1 -Disable
#
# What it does:
#  1. Standard page heap for ProsimCompanion.exe (what `gflags /p /enable ProsimCompanion.exe` writes):
#     fill patterns after every native allocation, checked on free, with allocation/free stack traces
#     kept per block — the dump then names the module that wrote past its buffer. Small overhead.
#     (-Full switches to full page heap: a guard page after every allocation, the process stops at
#     the exact write. Much more memory — only if standard finds nothing.)
#  2. Windows Error Reporting full dumps (DumpType 2) for ProsimCompanion.exe, at most 3 kept, in
#     %LOCALAPPDATA%\CrashDumps — a mini dump has no heap trace data.
param([switch]$Disable, [switch]$Full)

$ErrorActionPreference = 'Stop'
$ifeo = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\ProsimCompanion.exe'
$wer = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\ProsimCompanion.exe'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this as administrator (right-click PowerShell, Run as administrator).'
}

if ($Disable) {
    if (Test-Path $ifeo) {
        Remove-ItemProperty -Path $ifeo -Name GlobalFlag, PageHeapFlags -ErrorAction SilentlyContinue
        if ((Get-Item $ifeo).Property.Count -eq 0) { Remove-Item $ifeo }
    }
    if (Test-Path $wer) { Remove-Item $wer -Recurse }
    Write-Host 'Page heap OFF, per-app dump settings removed. Restart ProsimCompanion.'
    exit
}

New-Item -Path $ifeo -Force | Out-Null
Set-ItemProperty -Path $ifeo -Name GlobalFlag -Value 0x02000000 -Type DWord
Set-ItemProperty -Path $ifeo -Name PageHeapFlags -Value $(if ($Full) { 0x3 } else { 0x2 }) -Type DWord

New-Item -Path $wer -Force | Out-Null
Set-ItemProperty -Path $wer -Name DumpType -Value 2 -Type DWord
Set-ItemProperty -Path $wer -Name DumpCount -Value 3 -Type DWord

Write-Host ("Page heap {0} for ProsimCompanion.exe; full crash dumps (max 3) in %LOCALAPPDATA%\CrashDumps." -f $(if ($Full) { 'FULL' } else { 'STANDARD' }))
Write-Host 'Now restart ProsimCompanion. Page heap only applies to a process started after this.'
Get-ItemProperty $ifeo | Select-Object GlobalFlag, PageHeapFlags | Format-List
