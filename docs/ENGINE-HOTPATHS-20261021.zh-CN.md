# 2026-10-21 底层优化研究与实现

基线：已发布的 `performance-maintenance-20261019`，分支提交 `b63ba44`。
本轮构建：`engine-hotpaths-20261021`，仍是 0.4.0 实验源码；没有更新工坊。

## 问题分解与本轮选择

卡顿不是一个开关：客户端绘制/等待、服务器模拟超时、发送排队、预测状态差异、Lua 调用分配，以及加载/卸载的编译和生命周期，需要分别处理。
现有包擅长队列、附近查询、空 Lua 阶段和可回退的小范围并行；弱点是缺少同局服务器/客户端实战对照，且部分缓存自身仍有逐次审计成本。
本轮不提高纠正容忍度、不减慢模拟、不盲目扩大 worker。先改能执行原始方法并验证语义的高频与生成路径。

### 直接生成 LuaCs 包装

旧桥接先调用 LuaCs/Sigil 工厂生成一个包装，再生成额外桥接。活跃阶段仍经过原包装、重复查方法键和装箱的枚举器。
新工厂对已审计构建直接生成一个类型：使用预先建立的方法键，实时取回调注册表，类型化传递结果，调用具体字典值枚举器。
动态代码仍由 CLR 编译，参数表、原始参数字典和修改字典每次独立创建。模组可以保留这些对象，不能直接池化复用。

重要语义：回调顺序、ref/out、实例/构造器、OriginalReturnValue、ParameterTable.ReturnValue、Lua 返回、PreventExecution 和异常边界均与原包装对照。
源代码中的 `il.Break()` 是 IL 调试指令，不是 C# 的循环 break；不能据其拼写重写控制流。
结果对象的最终类型转换位于 LuaCs catch 外，错误返回类型仍抛出；参数写回的转换位于原有异常边界内。
其他模组后来修改原生回调枚举方法时，立即走它的枚举器，不绕过其逻辑。

`Settings.DirectLuaPatchFactory=false` 可恢复此前两阶段桥接。未知工厂、竞争工厂补丁或不支持的签名使用原生工厂。
卸下优化器但保留回调时，按需生成原生包装恢复挂接；这部分成本会发生在卸下阶段，因此生成测试不代表完整加载/卸载周期收益。
动态模块的引用释放不等于 CLR 元数据已立即回收。

### 实时状态标签索引

原 `HasStatusTag` 对每个条件标签遍历 DurationList/DelayList，反复搜索各效果的 Targets。
新路径只替换 `MatchesDirect` 中这一种条件，外层容器目标路由不变。
逐次核验实际列表版本、目标列表版本、标签集合引用/版本/数量；不假定同一帧内容不变。
只在相关状态没有变化时复用目标的标签集合。第一条效果足以证明全部标签命中时，保留短路，不审计后面无关的效果。
未知相等语义、自定义标签比较器及竞争补丁回退原方法；回合结束、插件清理以及工作量退到阈值以下时清理引用。
默认至少 32 条效果才尝试。此算法仍有 O(效果数) 的审计，首次建目标缓存和频繁变化也有成本，并不是 O(1) 的所有状态查询。

### 后台附近索引的集合所有权

原工作线程会枚举主线程仍能修改的 HashSet，再依赖异常/版本变化修复。HashSet 并不提供这种并发读写保证。
现在主线程捕获动态标签数组，工作线程只读该数组和不可变 prefab 标签；使用构建结果之前仍核验当前标签版本。
增加快照后修改原集合的确定性检查；它也明确了主线程初始拷贝有成本。

## 验证入口

`tools/Verify-Managed.py` 支持 SDK .NET 8 下的两端编译和独立方法检查，使用明确源文件快照、真实原生 DLL、Publicized 编译参考及输入哈希复核。
`tools/Fetch-AuditedDependencies.py` 只下载并接受固定 SHA256 的官方 LuaCs 包，避免可变的 latest 标签悄悄改变受测版本。
Linux 管理运行时不替代 Windows 自带运行时。完整 All 套件继续强制原物理指纹；官方包中的另一 Farseer 文件哈希不会被放行来制造通过。

Windows 离线入口现在分别使用客户端和 DedicatedServer 的 apphost/config/deps，服务端不再要求 MonoGame 客户端文件。
CI 在 Linux/Windows 执行独立检查；Windows 另用包内 CLR 和隔离 apphost。均不启动客户端/服务器 Main，不加载潜艇，不上传 DLL 或原始运行时。

```sh
python tools/Verify-Managed.py --client-dir CLIENT --server-dir SERVER
```

```powershell
./tools/Verify-Offline.ps1 -GameDir CLIENT -ServerGameDir SERVER -Suite Bridge
./tools/Verify-Offline.ps1 -GameDir CLIENT -ServerGameDir SERVER -Suite StatusTags
```

## 验收结果

受测源码 `88efaa7`，完整树 `e3796bc`。后续发布整理只改说明、打包与发布流程，不改受测游戏代码、兼容 Lua 或测试。
[GitHub Actions](https://github.com/laohei-moddeveloper/barotrauma-rebound-guard/actions/runs/39546286195) 的 Linux、Windows 两个任务通过。
两端各执行 Bridge、StatusTags、Nearby、Loading、Native、Lifecycle 六组；Windows 除 SDK 宿主外，另用游戏包内 CLR/apphost 执行两端全部六组。
本机云端另运行同样两端六组、14 项 Python 检查和可移植队列/帧/桶检查。
对应的[精简合成证据](validation/2026-10-21-engine-hotpaths.json)包含受测源文件 SHA256，不含游戏 DLL、原始实战记录或私人资料。

Lua 桥接每端 99 项语义检查，包含真实 MoonSharp 调用、保留参数表、重入、错误结果类型、后来挂接的外部枚举 hook、Reset 和恢复原包装。
状态索引每端 40,142 项状态比较，包含同一模拟步内的增删、同数量替换、清空、标签变化、重复项、空标签和竞争 hook。
附近查询包含已发布尾部维护与新增冻结快照检查。大量断言是合成状态比较，不是独立实战局数。

Linux SDK .NET 8 的 20,000 次回调包装 fixture：

| 实现 | 调用线程分配 |
| --- | ---: |
| 原 LuaCs | 16,480,000 字节 |
| 已发布两阶段桥接 | 7,040,000 字节 |
| 本轮直接生成 | 6,240,000 字节 |

相对已发布桥接减少约 **11.4%**；参数表仍每次独立分配。生成 fixture 也减少重复类型和 Sigil 工作，但不代表资源加载、编译和卸载总耗时。
状态查询的完整扫描 fixture 约缩短一半；早命中 fixture 有约数十纳秒/调用的额外检查，频繁修改也会重建缓存。具体值见 JSON。
跨系统运行 Windows 托管程序集的耗时受宿主/JIT/调度影响，不能据此给 Windows 游戏承诺倍数或整局收益。

原完整 All 套件在官方发布包的 Farseer 文件上被既有物理指纹拒绝；未扩大白名单来使其通过。上述六组通过不等于完整物理重放或实战通过。

## 借鉴与取舍

| 项目与固定来源 | 值得借鉴 | 本轮选择及限制 |
| --- | --- | --- |
| [ItemOptimizer](https://github.com/yosu-ovo/ItemOptimizer/tree/892e00ae0b79a52824209d2c86d3c28ba8296e78) | 减少高频重复调用、状态标签查询、网络 membership；功能可单独控制 | 采用实时状态核验；不照搬仅每帧清空的状态缓存。冻结远区、降低 AI/实体频率另有玩法取舍；梯子/平台补偿不能直接套用到所有回弹 |
| [Shadow Culling](https://github.com/zhu-rengong/barotrauma-mod-shadowculling/tree/797d42272592472fe1ffade57b6f17564ff67a6f) | 可见性筛选、绘制工作与包围盒成本分开处理 | 我们尚无绘制/GPU 实测；没有照搬归零 DrawSize 或视觉剔除规则。这是当前覆盖缺口 |
| [Lithium 变动追踪](https://github.com/CaffeineMC/lithium-fabric/blob/6cb5f0f06acb924fda3523ec661625a480ef0057/common/src/main/java/net/caffeinemc/mods/lithium/common/tracking/entity/SectionedEntityMovementTracker.java) | 按真实依赖失效，完整处理进入/离开和监听生命周期 | 潜渊症模组可直接改集合，写入契约不同；因此保留逐次版本核验，不能假称缓存命中后所有工作都是 O(1) |
| [Factorio 优化与并行](https://www.factorio.com/blog/post/fff-421) | 先剔除重复工作，再处理有共享关系的更新组；看完整阶段耗时 | 不以更高 CPU 占用充当收益，不直接把水体、电力、Lua VM 放进 Parallel.For |
| [Box2D 确定性](https://box2d.org/posts/2024/08/determinism/) | 多线程必须维护稳定顺序和可重现状态 | Box2D 3 的数据与 API 不能直接替换本项目 Farseer DLL；共享求解、碰撞回调、关节和连续碰撞仍需完整验证，保持研究开关关闭 |

本项目的优势是源码/运行时指纹、保留原生回退、客户端/服务器分离和真实方法对照；不足是实际热点覆盖有限、部分索引仍需要线性审计、没有网络包逐位对照、绘制优化或同局实战 A/B。
本轮没有复制其他项目实现，也没有拿其他游戏/模组的性能数字当作自己的收益。

## 发布与后续

用户已要求停止继续深挖，先发布 GitHub，再配置 Steam 凭据。工坊中文名保持不变；当前不上传 Steam。
后续拿到实战后再按服务器模拟时间、排队/确认延迟、可见回弹、真实帧间隔和加载分阶段数据决定下一步。
云端已完成本次必要 Windows 隔离检查，无须启动用户本机游戏；当前工具只有本地对话读取接口，未声称已向本地对话发送指令。
