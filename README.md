# DiskWriteMonitor · 磁盘写入量监控

一个 Windows 桌面小工具，按分区统计**每天的磁盘写入 / 读取字节数**，长期累计并以柱状图展示历史。

用 C# / .NET 8 WinForms 写成，磁盘数据来自系统性能计数器（PDH），无第三方依赖，也不需要管理员权限。

## 功能

- **按分区统计**：下拉框列出所有就绪卷，选择要监控的分区（一次一个）
- **今日用量**：当天写入量、读取量
- **累计用量**：自首次记录以来的总写入 / 总读取，以及已记录天数和起始日期
- **实时速度**：当前写入 / 读取速率，每秒刷新
- **历史视图**：最近 7 天、按月、或指定某一天；柱状图（蓝=写入，绿=读取）+ 明细表格 + 合计
- **托盘常驻**：关闭窗口不退出，最小化到托盘继续记录；双击图标恢复主界面
- **开机自启动**：写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
- **单实例**：命名互斥体保证只跑一个，重复启动会提示
- **按分区清空**：可单独清除某个分区的历史记录（有二次确认）

## 运行要求

| 项目 | 要求 |
|---|---|
| 系统 | Windows 10 / 11 x64 |
| 运行时 | 发布版自带 .NET 8 运行时，无需安装 |
| 权限 | 普通用户即可（`app.manifest` 里 `asInvoker`） |

## 数据存放位置

```
%LOCALAPPDATA%\DiskWriteMonitor\
├── records.json    # 各分区的每日记录
└── settings.json   # 上次选择的分区
```

`records.json` 结构：

```json
{
  "C:\\": [
    { "Date": "2026-09-01", "WriteBytes": 12345678, "ReadBytes": 8765432 }
  ]
}
```

## 实现要点

- **性能计数器**：PDH 的 `\PhysicalDisk(<实例>)\Disk Read Bytes/sec` 和 `Disk Write Bytes/sec`。注意实例名**不是盘符**，而是形如 `0 C:`、`1 D:` 的编号加盘符；程序先用 `PdhEnumObjectItems("PhysicalDisk")` 枚举实例，再按盘符匹配（`FindInstance` 同时兼容 `C:` 和 `0 C:` 两种形式）
- **累计口径**：每秒采样一次速率，乘以 `Stopwatch` 实测的间隔做积分，得到写入字节数。这是**软件侧累计**，不是硬盘 SMART 的硬件累计
- **持久化**：每 10 个采样周期（约 10 秒）落盘一次，正常退出时再存一次；跨天自动把增量记到新日期
- **图表**：`ChartPanel` 继承 `Panel` 重写 `OnPaint` 自绘，数据点多于 10/16 个时跳绘 X 轴标签，避免文字重叠
- **关闭行为**：`FormClosing` 里 `e.Cancel = true` 并隐藏窗口，真正退出走托盘菜单的「退出」

## 已知限制

- **只有程序运行时才采样**：关机、程序未启动期间的数据无法补录，这也是界面里明确提示的
- **同时只监控一个分区**：切换分区后，之前的分区停止记录（历史数据保留）
- **部分卷取不到计数器**：例如某些动态卷或网络位置，下拉框会标注「无法读取计数器」，该分区无法记录
- **速率来自系统计数器**：写入缓存尚未落盘的瞬间可能已经计入，和硬盘固件统计会有细微差异

## 构建

`csproj` 里没有写发布参数，需要用命令行指定单文件自包含发布：

```bat
dotnet publish -c Release -o publish -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true
```

产物是 `publish\DiskWriteMonitor.exe`，单个文件约 66 MB（内置 .NET 运行时，已启用压缩）。

## 目录结构

```
DiskWriteMonitor/
├── Program.cs              # 全部代码：数据存储 / PDH 采样 / 图表 / 主窗体
├── DiskWriteMonitor.csproj # net8.0-windows + WinForms
├── app.manifest            # asInvoker + 声明支持 Win10/11
├── smoketest.ps1           # 冒烟测试①：启动、制造写入、校验落盘、单实例检查
├── smoketest2.ps1          # 冒烟测试②：干净起点启动，观察 settings/records 变化
├── smoketest3.ps1          # 冒烟测试③：同上，写入节奏更慢，便于观察采样
└── bin/ obj/ publish/      # 构建产物（.gitignore 已排除）
```

三个 `smoketest*.ps1` 都按**脚本自身所在目录**定位 `publish\DiskWriteMonitor.exe`，并会清空 `%LOCALAPPDATA%\DiskWriteMonitor` 后启动程序、对磁盘制造真实写入、再回读 `records.json` 校验。它们会启动 GUI 并驱动进程，属于手动运行的诊断脚本，不要在无人值守的场合随便跑。
