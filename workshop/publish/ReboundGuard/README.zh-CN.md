# Rebound Guard 0.4.1：1.13.5.0 兼容与 LuaCs 参数回写修复

本版沿用 2026-10-21 已发布主线的实时状态标签索引、跨发送周期队列缓存、单次生成 LuaCs 包装、附近物品增量索引和客户端帧诊断。没有把这些已有能力退回到 0.4.0 最初版本。

新增内容：

- 识别 2026-10-27 LuaCs 官方 Windows 客户端/服务端 1.13.5.0 的完整运行时程序集 SHA256。未知构建继续回退；没有只放宽版本号。
- 参数回写恢复原生隐式转换规则，缓存类型对的转换方法查询，常见同类型参数直接回写。实际转换运算仍逐次执行；不缓存游戏状态。
- 第三方抑制或修改 `ClearAll` 时保留仍活跃的包装状态；保留已有的延迟枚举挂钩回退。
- 单项补丁安装失败只撤销该项本次增加的补丁，保留已成功安装的其他优化。
- 安装器支持当前 LuaCs 的 `CsRunPolicy` 设置格式，安装目录改为独立的 `ReboundGuard_Experimental_041`。

## 安装和确认

先按 LuaCs 官方说明安装与游戏相配的运行时。关闭游戏和服务器后运行：

```powershell
pwsh -NoProfile -File .\Install.ps1 -GameDir '你的 Barotrauma 目录'
```

也可手动复制 `LocalMod/ReboundGuard` 到游戏 `LocalMods` 下的独立目录。启用 C#、停用所有旧 Rebound Guard 副本、启用新版并完整重启。安装器不替换游戏 DLL，不改写 C# 运行策略，也不覆盖已有 041 目录。当前 LuaCs 的客户端提示仍需在游戏内确认。

F3/本地报告应出现 `version=0.4.1; build=compat-1.13.5-20261031`。房主客户端、实际服务器进程及需要本机收益的玩家分别加载。只看到客户端报告不代表服务端已启用。

可选 Mudraptor/TSM 移植仍保持原有 040 备份格式和完整源文件校验；本版不自动修改其他模组。已安装的匹配桥接可继续使用。

## 实测与边界

详见 [本轮报告](https://github.com/laohei-moddeveloper/barotrauma-rebound-guard/blob/v0.4.1/docs/OPTIMIZATION-0.4.1.zh-CN.md) 与 `docs/validation/0.4.1/` 中的独立结果。

测试分别记录 LuaCs 调用、队列、引用卸载、附近查询和完整物理步；局部加速不能换算成整局 FPS。Bool 返回基准还包含原生 IL 调试指令的宿主相关成本，应同时查看不含该指令的 void/ref 参数基准。

本版没有实战联机、渲染或完整加载 A/B 数据，不能宣称整局十倍，也不能宣称所有卡顿、回弹、加载慢彻底消失。服务器权威位置、输入裁剪、模拟/AI/效果频率及物理解算迭代数保持原规则；位置投影和共享速度求解研究仍默认关闭。

本轮按用户后续指令提前结束一小时耐久计划；最终版本另做短时完整结束的压力检查。此前原型的中断记录不作为最终包的耐久通过证据。

## 撤回与开发验证

停用 041 包并完整重启可撤回通用补丁；可选移植按原说明用 `Install-KnownModFixes.ps1 -Restore` 恢复。不要同时启用两个 Rebound Guard 副本。

```bash
python tools/Verify-Portable.py --game-dir YOUR_LUACS_BUILD --side Client --suite Compatibility
python tools/Verify-Portable.py --game-dir YOUR_LUACS_SERVER_BUILD --side Server --suite Dispatch
```

完整物理回归要求至少四个逻辑处理器。小型 CI 可显式加 `--exercise-parallel` 覆盖调度路径；该选项不会增加物理 CPU，结果中会注明。Windows 游戏包内运行时验证使用 `tools/Verify-Offline.ps1`，传入各端独立的匹配目录。
