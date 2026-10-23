$ErrorActionPreference = 'Continue'

# 1. 清理旧的实例与测试数据
Get-Process DiskWriteMonitor -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\DiskWriteMonitor" -ErrorAction SilentlyContinue
Write-Output 'STEP1_CLEAN_DONE'

# 2. 启动程序
$Root = $PSScriptRoot
$exe = Join-Path $Root 'publish\DiskWriteMonitor.exe'
$p = Start-Process -FilePath $exe -PassThru
Write-Output ("STEP2_LAUNCHED_PID=" + $p.Id)
Start-Sleep -Seconds 15
$alive = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
Write-Output ("STEP3_ALIVE_AFTER_15S: " + [bool]$alive)

# 3. 制造真实磁盘写入（60MB 分 6 秒写入，强制 Flush 落盘）
$path = Join-Path $env:TEMP 'dwm_test.bin'
$fs = [System.IO.File]::OpenWrite($path)
$buf = New-Object byte[] (1048576)
(New-Object Random).NextBytes($buf)
for ($i = 0; $i -lt 60; $i++) {
    $fs.Write($buf, 0, $buf.Length)
    $fs.Flush()
    Start-Sleep -Milliseconds 100
}
$fs.Close()
Remove-Item -Force $path
Write-Output 'STEP4_WRITES_DONE'
Start-Sleep -Seconds 3

# 4. 校验记录文件
$dir = "$env:LOCALAPPDATA\DiskWriteMonitor"
Write-Output ("STEP5_DIR_EXISTS: " + (Test-Path $dir))
if (Test-Path $dir) {
    Get-ChildItem $dir | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
    $rec = Join-Path $dir 'records.json'
    if (Test-Path $rec) { Write-Output 'RECORDS_JSON:'; Get-Content $rec -Raw }
}

# 5. 单实例验证：再启动一个，应弹提示框；用回车关闭提示框
Start-Process -FilePath $exe
Start-Sleep -Seconds 3
$wshell = New-Object -ComObject WScript.Shell
$wshell.SendKeys('{ENTER}')
Start-Sleep -Seconds 2
$count = @(Get-Process DiskWriteMonitor -ErrorAction SilentlyContinue).Count
Write-Output ("STEP6_PROCESS_COUNT_AFTER_SECOND_LAUNCH: " + $count)

# 停留，便于人工查看 UI（不做清理，交还人工）
Write-Output 'TEST_DONE'