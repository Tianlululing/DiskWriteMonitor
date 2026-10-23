$ErrorActionPreference = 'Continue'

# 干净起点
Get-Process DiskWriteMonitor -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\DiskWriteMonitor" -ErrorAction SilentlyContinue
Write-Output ("CLEANED, DIR_EXISTS=" + (Test-Path "$env:LOCALAPPDATA\DiskWriteMonitor"))

# 全新启动
$Root = $PSScriptRoot
$exe = Join-Path $Root 'publish\DiskWriteMonitor.exe'
$p = Start-Process -FilePath $exe -PassThru
Write-Output ("LAUNCHED PID=" + $p.Id)
Start-Sleep -Seconds 14

# 读当前落盘情况（应只有一个分区被初始化）
Write-Output "--- SETTINGS ---"
Get-Content "$env:LOCALAPPDATA\DiskWriteMonitor\settings.json" -Raw -ErrorAction SilentlyContinue
Write-Output "--- RECORDS BEFORE WRITES ---"
Get-Content "$env:LOCALAPPDATA\DiskWriteMonitor\records.json" -Raw -ErrorAction SilentlyContinue

# 制造真实写入：直接写 100MB 到 C 盘临时文件（用固定路径，避开 TEMP 环境问题）
$path = Join-Path $Root '_weirte_test.bin'
$fs = [System.IO.File]::OpenWrite($path)
$buf = New-Object byte[] (1048576)
(New-Object Random).NextBytes($buf)
for ($i = 0; $i -lt 100; $i++) {
    $fs.Write($buf, 0, $buf.Length)
    $fs.Flush()
    Start-Sleep -Milliseconds 80
}
$fs.Close()
Remove-Item -Force $path
Write-Output "--- WRITES DONE ---"
Start-Sleep -Seconds 4

Write-Output "--- RECORDS AFTER WRITES ---"
Get-Content "$env:LOCALAPPDATA\DiskWriteMonitor\records.json" -Raw -ErrorAction SilentlyContinue
Write-Output 'TEST_PART2_DONE'