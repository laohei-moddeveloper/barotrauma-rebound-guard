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

实测数据、外部项目对照、完整验收范围及本地接续要求在本轮结束前补齐。未取得的实战证据不会用局部基准代替。
