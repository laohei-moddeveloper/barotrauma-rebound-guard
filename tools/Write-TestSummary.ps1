$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$lines = [Collections.Generic.List[string]]::new()
@('# 0.4.0 原生测试摘要', '', '数据来自最近一次 validation JSON。运行时为游戏自带 .NET 8.0.0，目标程序集 1.13.4.0；mod 与测试明确 Release 编译，隔离宿主关闭分层 JIT。', '', '本轮自动测试未启动真实潜艇战斗或多人联机。第二轮实战证据见《第二轮分析与通用优化-20261018.md》。不能把局部查询倍数当作整局 FPS/回弹改善幅度。编译存在 Harmony 旧 ILGeneration 引用的 CS1701 警告，已完成原生宿主挂接与执行检查。', '') | ForEach-Object { $lines.Add($_) }
function Number($value) { ([double]$value).ToString('F4', [Globalization.CultureInfo]::InvariantCulture) }
foreach ($side in @('client','server')) {
    $data = Get-Content -Raw -LiteralPath (Join-Path $root ('research\deep\validation-' + $side + '.json')) | ConvertFrom-Json
    $lines.Add('## ' + $side + '：' + $data.date)
    $lines.Add('')
    $g=$data.generic_lua_bridge
    $l=$data.loading_lifecycle
    $n=$data.native_mod_hot_paths
    $lines.Add('### 通用桥接、加载与可选兼容')
    $lines.Add('')
    $lines.Add('| 检查 | 原版 | 优化 | 范围 |')
    $lines.Add('| --- | ---: | ---: | --- |')
    $lines.Add('| LuaCs 调用包装 20,000 次 ms | '+(Number $g.native_luacs_ms)+' | '+(Number $g.optimized_bridge_ms)+' | '+$g.checks+' 项回调语义检查，不含 Lua VM |')
    $lines.Add('| 上述分配 bytes | '+$g.native_luacs_bytes+' | '+$g.optimized_bridge_bytes+' | 分配减少约 57.3%，不是整局分配 |')
    $lines.Add('| 提前结束枚举后卸载 ms | '+(Number $l.native_dispose_ms)+' | '+(Number $l.optimized_dispose_ms)+' | '+$l.checks+' 项；原生 GC 保留 |')
    $lines.Add('| 可选 IsHuman 规则 10,000 次 ms | '+(Number $n.lua_rule_ms)+' | '+(Number $n.native_getter_ms)+' | '+$n.checks+' 项；原生移植仅适用审计过的 Lua 源码 |')
    $lines.Add('')
    $lines.Add('LuaCs 保留所有实际回调、参数/返回值修改、阻止执行、顺序、异常、Reset 与撤回行为。测试覆盖引用/值/可空返回、ref/out、构造与实例。动态模块重置验证只证明释放了旧 builder 引用，不证明全部 CLR 元数据立即回收。加载测试复现单个提前结束枚举导致的两秒等待，不代表完整关卡载入时间。可选 IsHuman 基线包含真实 MoonSharp，但省略完整 LuaCs 外层包装。')
    $lines.Add('')
    $lines.Add('逻辑处理器 ' + $data.processors + '；主回归断言 ' + $data.assertions + '；有序约束 ' + $data.ordered_solver_research.checks + ' 项；初始化 ' + $data.constraint_preparation.checks + ' 项；附近查询 ' + $data.nearby_item_index.checks + ' 项；容器 ' + $data.container_effect_guard.checks + ' 项。主回归包含逐字段重复比较，不等于独立游戏场景数。')
    $lines.Add('')
    $lines.Add('### 完整附近查询')
    $lines.Add('')
    $lines.Add('附近物品密集、目标标签有选择性的合成场景，连续 100 次完整查询，包括距离、原生有效性、属性对象收集和每查询真实标签审计。基线未挂接。单位 ms。')
    $lines.Add('')
    $lines.Add('| 物品数 | 原版 100 次 | 优化 100 次 | 本阶段加速比 |')
    $lines.Add('| ---: | ---: | ---: | ---: |')
    foreach ($r in $data.nearby_item_index.benchmarks) { $lines.Add('| ' + $r.items + ' | ' + (Number $r.unpatched_100_queries_ms) + ' | ' + (Number $r.indexed_100_queries_ms) + ' | ' + (Number ($r.unpatched_100_queries_ms / $r.indexed_100_queries_ms)) + ' |') }
    $lines.Add('')
    $lines.Add('反例：全部远处物品与目标覆盖过宽。回退仍有入口/选择成本，数值含调度噪声，不宣称每种分布都提速。单位 ms/查询。')
    $lines.Add('')
    $lines.Add('| 物品数 | 远处原版 | 远处回退 | 宽目标原版 | 宽目标回退 |')
    $lines.Add('| ---: | ---: | ---: | ---: | ---: |')
    foreach ($r in $data.nearby_item_index.benchmarks) { $lines.Add('| ' + $r.items + ' | ' + (Number $r.far_native_ms) + ' | ' + (Number $r.far_indexed_ms) + ' | ' + (Number $r.dense_native_ms) + ' | ' + (Number $r.dense_fallback_ms) + ' |') }
    $lines.Add('')
    $lines.Add('首次查询包括捕获快照与原查询。后台期间仍走原查询；等待列是测试宿主从首次查询后到采用索引的观察时间，游戏代码不等待。单位 ms。')
    $lines.Add('')
    $lines.Add('| 物品数 | 首次查询 | 首次查询后到可用 | 增加一个后的查询 | 删除一个后的查询 |')
    $lines.Add('| ---: | ---: | ---: | ---: | ---: |')
    foreach ($r in $data.nearby_item_index.benchmarks) { $lines.Add('| ' + $r.items + ' | ' + (Number $r.cold_query_ms) + ' | ' + (Number $r.background_ready_delay_ms) + ' | ' + (Number $r.append_query_ms) + ' | ' + (Number $r.remove_query_ms) + ' |') }
    $lines.Add('')
    $lines.Add('### 完整物理步')
    $lines.Add('')
    $lines.Add('保留原 ' + $data.world_performance.velocity_iterations + ' 次速度、' + $data.world_performance.position_iterations + ' 次位置迭代。每场景 130 步状态逐位一致。每步前重置物体维持接触，重置/比较不计时；这不是潜艇整帧。两次原版基线夹住优化/挂接串行。单位 ms 中位数。')
    $lines.Add('')
    $lines.Add('| 场景 | 接触数 | 原版一 | 并行初始化 | 挂接串行 | 原版二 | 并行调用数 |')
    $lines.Add('| --- | ---: | ---: | ---: | ---: | ---: | ---: |')
    foreach ($r in $data.world_performance.rows) { $lines.Add('| ' + $r.scene + ' | ' + $r.contacts + ' | ' + (Number $r.unpatched_median_ms) + ' | ' + (Number $r.parallel_median_ms) + ' | ' + (Number $r.patched_serial_median_ms) + ' | ' + (Number $r.unpatched_second_median_ms) + ' | ' + $r.parallel_prepare_calls + ' |') }
    $lines.Add('')
    $h = $data.constraint_preparation.actual_hook_benchmark
    $lines.Add('初始化局部真实挂接，4096 条接触：原版 ' + (Number $h.unpatched_ms) + ' ms，挂接串行 ' + (Number $h.patched_serial_ms) + ' ms，并行 ' + (Number $h.patched_parallel_ms) + ' ms。局部收益不等于完整物理步收益。')
    $lines.Add('')
    $lines.Add('### 连续世界状态与回调')
    $lines.Add('')
    $lines.Add('| 路径 | 场景 | 步数 | 生成/删除物理体 | 初始关节 | 碰撞回调 |')
    $lines.Add('| --- | --- | ---: | --- | ---: | ---: |')
    foreach ($s in $data.world_replay.scenes) { $lines.Add('| ' + $s.mode + ' | ' + $s.scene + ' | ' + $s.steps + ' | ' + $s.bodies_created + '/' + $s.bodies_removed + ' | ' + $s.joints_created + ' | ' + $s.collision_callbacks + ' |') }
    $lines.Add('')
    $lines.Add('所有上述连续世界每步位置、角度、线/角速度、接触数与碰撞回调顺序一致。测试降低门槛以执行路径；有序速度求解仍默认关闭。')
    $lines.Add('')
    $lines.Add('整包生命周期：' + $data.plugin_lifecycle.initialization + '；' + $data.plugin_lifecycle.dispose + '；' + $data.plugin_lifecycle.diagnostic_writer + '。未启动游戏入口。')
    $lines.Add('重载：' + $data.plugin_lifecycle.reload + '。')
    $lines.Add('')
    $r = $data.field_iteration
    $lines.Add('### 实战后迭代检查')
    $lines.Add('')
    $lines.Add('新增 ' + $r.checks + ' 项检查：五类补丁 owner、按需扫描、逐次计时与异常保留、直方图、纠正目标/历史误差区分。')
    $lines.Add('')
    $lines.Add('| 局部微基准 | 旧写法 ns | 新写法 ns | 旧写法每千次分配 bytes | 新写法每千次分配 bytes |')
    $lines.Add('| --- | ---: | ---: | ---: | ---: |')
    $lines.Add('| owner 分组判定 | ' + (Number $r.owner_inspection_legacy_ns) + ' | ' + (Number $r.owner_inspection_direct_ns) + ' | ' + $r.owner_inspection_legacy_bytes_per_1000 + ' | ' + $r.owner_inspection_direct_bytes_per_1000 + ' |')
    $lines.Add('| 无适用工作的两个作用域入口 | ' + (Number $r.legacy_idle_scope_ns) + ' | ' + (Number $r.lazy_idle_scope_ns) + ' | ' + $r.legacy_idle_scope_bytes_per_1000 + ' | ' + $r.lazy_idle_scope_bytes_per_1000 + ' |')
    $lines.Add('')
    $lines.Add('空方法未挂接/逐次计时 ns：' + (Number $r.empty_method_ns) + '/' + (Number $r.empty_method_with_profiler_ns) + '。含 Harmony 开销；真实粗粒度调用次数/层级不同，不能直接换算整局耗时。owner 判定不含 GetPatchInfo 本身的包装分配；空闲入口在完整插件已加载时测量，但无真实游戏逻辑。')
    $lines.Add('')
}
$lines.Add('## 边界')
$lines.Add('')
$lines.Add('索引不缓存位置；标签校验不依赖会被 JIT 内联绕过的方法通知。覆盖直接 HashSet 变动、列表重排、后台陈旧结果丢弃、重复属性对象和冲突退回。类型/比较器/二进制不受支持时保持原查询，任意其他模组的不安全内存改写与并发行为未证明兼容。')
$lines.Add('')
$lines.Add('原始 JSON 还保留维护循环、队列、容器无效搜索与默认关闭的求解研究微基准。尚未验证真实潜艇、角色动画、完整 Lua 模组组合、长时间联机及全部 TOI 极端情形。实际回弹改善需实战检验。')
[IO.File]::WriteAllLines((Join-Path $root '研究测试摘要.md'), $lines, [Text.UTF8Encoding]::new($false))
Write-Output 'PASS: 0.4.0 summary generated from both native runtime reports.'
