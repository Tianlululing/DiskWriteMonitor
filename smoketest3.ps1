$ErrorActionPreference = 'Continue'

Get-Process DiskWriteMonitor -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\DiskWriteMonitor" -ErrorAction SilentlyContinue

$Root = $PSScriptRoot
$exe = Join-Path $Root 'publish\DiskWriteMonitor.exe'
$p = Start-Process -FilePath $exe -PassThru
Write-Output ("LAUNCHED PID=" + $p.Id)
Start-Sleep -Seconds 14
Write-Output "--- SETTINGS ---"
Get-Content "$env:LOCALAPPDATA\DiskWriteMonitor\settings.json" -Raw -ErrorAction SilentlyContinue
Write-Output "--- RECORDS BEFORE ---"
Get-Content "$env:LOCALAPPDATA\DiskWriteMonitor\records.json" -Raw -ErrorAction SilentlyContinue

$path = Join-Path $Root '_write_test.bin'
Write-Output ("PATH=[" + $path + "]")
$fs = [System.IO.File]::OpenWrite($path)
$buf = New-Object byte[] (1048576)
$rnd = New-Object Random
for ($i = 0; $i -lt 100; $i++) {
    $rnd.NextBytes($buf)
    $fs.Write($buf, 0, $buf.Length)
    $fs.Flush()
    Start-Sleep -Milliseconds 60
}
$fs.Close()
Remove-Item -Force $path -ErrorAction SilentlyContinue
Write-Output "--- WRITES DONE ---"
Start-Sleep -Seconds 4

Write-Output "--- RECORDS AFTER ---"
Get-Content "$env:LOCALAPPDATA\DiskWriteMonitor\records.json" -Raw -ErrorAction SilentlyContinue
Write-Output 'PART2_DONE'