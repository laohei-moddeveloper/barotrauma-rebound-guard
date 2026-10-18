# Rebound Guard 0.4.0 使用说明

## 通用安装

适用已核验的 1.13.4.0 / LuaCs 方法构建。未知方法或竞争补丁会使相应功能回退并记录原因；版本号相同不代表 DLL 完全相同。

1. 关闭游戏和服务器，停用所有旧版 Rebound Guard。
2. 保留兼容的 LuaCs，开启 C# scripting。
3. 解压后在该目录打开 PowerShell，运行下方命令；或把 `LocalMod/ReboundGuard` 整个目录复制为游戏目录中的 `LocalMods/ReboundGuard_Experimental_040`。
4. 启用 **Rebound Guard 0.4.0 (Experimental)**，完整重启。

```powershell
.\Install.ps1 -GameDir 'E:\steam\steamapps\common\Barotrauma'
```

房主客户端、实际服务器进程、希望得到本机优化的玩家均应安装。游戏内开房时，检查实际服务器是否生成带 `server` 的报告，并出现 `server position queue` / `server input backlog`；房主客户端日志不能替代它。各进程应显示 `loaded; version=0.4.0`。互联仍受游戏/LuaCs 的内容包规则约束。

安装器拒绝覆盖已有 040 目录，不修改游戏 DLL、存档、配置或模组顺序。开发中未把新版写入正在使用的游戏或模组；请保留原有旧版目录，停用新版后可重新启用旧版回退。

## 通用改动

| 功能 | 处理的问题 | 边界 |
| --- | --- | --- |
| LuaCs 桥接 | 空回调阶段仍构造参数表、每次取得元数据、外层装箱 | 所有实际回调仍通过原生成体执行 |
| 引用枚举生命周期 | 提前结束后计数未释放，卸载白等两秒 | 保留原枚举、Dispose 和全堆 GC |
| 动态模块重置 | Reset 后一直沿用旧动态模块 builder | 释放旧引用，未量化整场内存收益 |
| 客户端退出顺序 | LuaCs 释放后，Quit 恢复内容仍调用事件服务 | 仅已知 OnExiting；本次房主事故仍缺异常栈 |
| 分配/崩溃证据 | 仅有大步骤耗时，无法定位分配和退出原因 | 本线程分配的父子统计重叠 |

保留旧版 FIFO 队列辅助索引、附近标签索引、无效容器搜索跳过及独立阶段并行。默认最多 4 个 worker；共享物理求解和位置投影研究仍关闭。

## 可选：已审计 Lua 热点移植

仅用通用功能时，不必运行本节。可选原生实现保留 Mudraptor IsHuman 的“原结果或实时 UseHumanAI”，以及 TSM 三种实时增益对移动惩罚/瞄准抖动的规则。没有删除增益或角色行为。

从启用配置识别包目录并一并安装：

```powershell
.\Install-All.ps1 -GameDir 'E:\steam\steamapps\common\Barotrauma'
```

也可明确指定当前启用的包根目录，避免改错副本：

```powershell
.\Install-KnownModFixes.ps1 -PackageDir '你的 Mudraptor 包目录','你的 TSM 包目录'
```

脚本核对包名和完整原文 SHA256，只替换审计过的源文件，保留字节一致的 `.ReboundGuard040.original`。不同版本或自行修改会拒绝覆盖；工坊更新后需要重新核验。请在玩家与实际服务端使用一致修改。原生入口不可用或存在竞争补丁时，桥接注册原有 Lua 规则。

撤回可选移植：关闭所有进程，用相同包目录运行：

```powershell
.\Install-KnownModFixes.ps1 -PackageDir '你的 Mudraptor 包目录','你的 TSM 包目录' -Restore
```

脚本拒绝覆盖安装后又被修改的文件。通用包撤回则停用/移走独立 040 目录并完整重启。

## 下一轮测试和报告

保持潜艇、存档、模组、玩家数与战斗负载接近，分别记录旧版和新版；记下进局耗时、明显卡顿和崩溃时间。平均 FPS、模拟耗时、输入积压、渲染尖峰需分别比较。

报告在各进程运行目录的 `ReboundGuardReports`，本机是：

```text
E:\steam\steamapps\common\Barotrauma\ReboundGuardReports
```

房主原日志显示游戏在 D 盘。若游戏再次直接消失，请尽快在房主机器的解压目录运行：

```powershell
.\Collect-Reports.ps1 -GameDir 'D:\steam\steamapps\common\Barotrauma' -Hours 3
```

输出为解压目录下的 `CollectedReports_时间.zip`，包括近期报告、异常旁路文件、游戏 crashreport 和相关 Windows Application 错误。旧 crashreport 只列时间，避免错当本次证据；不改注册表、转储策略或游戏配置。原生崩溃、强杀等仍可能没有托管异常文件。

- `generic Lua bridge`：生成包装数、实际避开的空阶段、释放的上下文；未生成包装时不应假定已获收益。
- `memory`：托管存活量、上次 GC 堆、碎片、私有内存和工作集；分配量不等于泄漏。
- `thread_allocated_mb`：该阶段调用线程分配；父子不能相加，不含其他线程。
- `native mod hot paths`：可选移植安装与调用量；仅装通用包时显示 false 正常。
- `reference enumeration lifetime fix installed` / `exit lifetime fix installed`：确认实际构建是否匹配。

`large_corrections` 仍是纠正分支计数，不是可见回弹次数。其余指标解释见旧版说明。

## 开关与复核

设置在 `CSharp/Shared/Settings.cs`，改后完整重启。新项为 `OptimizeLuaBridge`、`RecycleLuaPatchModules`、`FixReferenceEnumerationLifetime`、`FixExitDisposalOrder`；可选移植为 `NativeKnownModHotPaths`。诊断对照需同时关闭旧版五项优化和这些新项，保留 `Diagnostics`，旧版五项名称见 0.3.1 说明。诊断有少量开销。

`tools/Verify-Deep.ps1 -Side Client` / `-Side Server` 使用本地依赖，在隔离目录运行真实游戏方法、LuaCs、MoonSharp 和物理库；不启动真实潜艇或联网。`-Suite Bridge`、`-Suite Loading`、`-Suite Native` 分别复核新路径。EventPipe 工具另需诊断 NuGet 依赖，未分发第三方二进制。
