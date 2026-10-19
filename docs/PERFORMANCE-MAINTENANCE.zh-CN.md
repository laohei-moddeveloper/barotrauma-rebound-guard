# Windows 隔离验证后的第二批性能改动

日期：2026-10-19。源码基线为 `b9190d4`，其中记录了 `6230d69` 的 Windows Client / LuaCs Server 编译与完整隔离检查。本轮研究构建标识为 `performance-maintenance-20261019`。

范围继续为多人客户端与实际服务器；不启动游戏客户端或服务器入口，不安装模组或改写游戏 DLL。

## 从新证据选择目标

[本机报告](validation/2026-10-19-windows-pr1.md)证明了上一批改动的完整编译与隔离执行，也确认实际 LuaCs Server 的依赖与客户端根目录不同。它仍不是联机、渲染或游戏加载 A/B。

原生附近查询合成场景中，16,384 个物品增加/删除一个后的查询，Client 约 3.91 / 4.13 ms，Server 约 5.95 / 4.68 ms。这些单次计时有噪声，但足以要求进一步对照结构维护成本，而不是只继续展示稳定缓存查询。
队列的首次仅入队场景也显示计数表有额外开销，因此本轮减少字典探测。

## 本体附近索引：验证共享前缀后维护尾部

原实现每次 List 版本变化都扫描字典、建立下一份节点列表、核对删除项、清空所有候选，并在需要时排序。本轮增加最多 64 项的尾部变化路径：

- 逐项比较当前 List 与已索引节点的所有共享引用。版本差/数量差不能替代这一步：一次 Remove 和一次 AddRange(3) 可以产生与两次 Add 相同的差值，却改变中间序列。
- 纯追加先完整校验并暂存新尾部，拒绝旧项重复、新项重复及不支持的物品/标签类型。然后只加入新节点与相关标签桶，保留其他候选缓存和原有相对顺序。
- 纯尾部删除逆序维护桶尾，避免逐桶线性 Remove。删除后清空全部候选引用：某个过期候选可能仍在“旧标签”下持有已删除物品，不能只根据该物品当前标签清理。
- 原列表内容未变、只有版本变化时更新版本记录；逐次真实标签审计仍照常执行。
- 中间插入/删除、重排、超过 64 项的变化及不支持状态继续使用完整维护。

该路径仍有 O(n) 前缀核对和逐次标签审计，**不是 O(1) 查询**。它消除的是结构变化时额外的重复工作。新增 `tail_appends`、`tail_removals`、`unchanged_sequences`、`prefix_items_checked` 计数。

`Settings.IncrementalNearbyListMaintenance` 可关闭新路径，作为同一构建的对照。原生套件增加尾部、旧标签引用释放、误导性版本差、大批变化、完全相同序列等语义检查；每种物品规模还会对“增加后查询 + 删除后查询”执行完整维护前测、新路径和完整维护后测，字段为：

```text
append_remove_queries_full_before_ms
append_remove_queries_incremental_ms
append_remove_queries_full_after_ms
```

这些新增原生检查和比较尚未在本轮 Windows 游戏宿主执行，不能把上轮通过结果转移到新路径，也不宣称已降低实际多人帧时间。

## 服务端队列：一次探测维护计数

使用 .NET 8 已提供的 `CollectionsMarshal` 访问计数值，避免每次先查找再按索引写入。不会把值引用跨字典修改保存。重复计数、null、版本失效、FIFO、弱键和回合清理行为保留；找不到被出队键时保持失效，下一次查询重建。

本轮 .NET 8.0.32 可移植检查通过，队列共 110,756 项状态断言。实际生产 helper 的结构计数为：

| 工作 | 键哈希次数 |
| --- | ---: |
| 4096 项重建及一次 Contains | 4097 |
| 1000 次重复项入队 | 1000 |
| 1000 次非末项重复项出队 | 1000 |

旧写法的重建/入队各需两次探测，非末项重复项出队需三次。这里只证明重复工作减少，不换算 FPS 或回弹收益。FrameHealth 的 11 项检查与 BucketStamp 的 143,780 项比较仍通过。

## 新的离线验证入口

`tools/Verify-Offline.ps1` 为每次调用建立独立的 `build/offline-*` 源码快照、构建目录、日志和报告。只复制明确允许的 C# 测试/模组源码、Lua 兼容源码与两份验证脚本；不复用或删除旧运行时，不覆盖历史验证 JSON。

两端检查强制明确 Server 依赖目录，避免把客户端根目录的原版 DedicatedServer 当成 LuaCs Server。以本机报告已定位的目录为例：

```powershell
$client = 'D:\steam\steamapps\common\Barotrauma'
$server = Join-Path $env:LOCALAPPDATA 'Daedalic Entertainment GmbH\Barotrauma\WorkshopMods\Installed\2559634234\Binary'
pwsh -NoProfile -File tools/Verify-Offline.ps1 -GameDir $client -ServerGameDir $server
```

默认在分别启动的 PowerShell 进程中编译并运行两端深度隔离宿主。`TestRunner.exe` 使用测试入口，不调用游戏/服务器 Main；没有真实潜艇、画面或联机。依赖文件在开始和结束时记录/复核哈希，源码快照也有指纹，报告全部留在忽略的 build 目录。

- `-PrepareOnly`：仅验证文件存在、记录哈希和准备快照，状态为 `prepared_not_tested`，不验证 DLL 兼容性、不编译/运行宿主。
- `-CompileOnly`：只编译，状态为 `compiled_not_executed`，不冒充运行通过。
- `-Side Client`：只检查 Client；`-Side Server -GameDir $server`：明确只检查该 Server 目录。
- 非 Windows 系统只允许 PrepareOnly。

云端用官方 PowerShell 7.4.7 对合成文件夹执行了 7 个入口测试：双目录分离、缺失依赖拒绝、旧缓存/私人文件排除、唯一快照、只准备无执行及非 Windows 拒绝。并通过 PowerShell AST 解析。合成依赖不是有效游戏 DLL，这些测试不会运行游戏代码，也不能当作本轮 Windows 编译/挂接结果。

## 下一次本机验证

运行上述入口，检查两端完整套件，确认新维护路径与原生目标列表顺序相同、旧标签下的引用清除、回退仍有效，并读取新增三组维护比较数据。保留 `manifest.json`、两端日志和新 JSON。当前本机不能自动从云端接入；切换权限已经由用户授予，但实际执行仍需要可用的本机工具入口。

无需启动游戏。真实联机、输入确认/发送排队年龄、渲染/GPU 和加载分阶段问题仍属于后续实战验收范围。
