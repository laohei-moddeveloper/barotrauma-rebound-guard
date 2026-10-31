# 深潜稳帧 / Rebound Guard 0.4.1 测试版

面向已审计的 Windows Barotrauma 1.13.5.0 / LuaCs 2026-10-27 构建。保留 10 月 21 日主线已有优化，新增精确版本适配、LuaCs 参数转换缓存与原生语义修复、单项安装失败回退，以及新版 LuaCs 设置识别。

- [安装、确认和撤回](README-0.4.1.zh-CN.md)
- [具体改动、实测幅度与边界](docs/OPTIMIZATION-0.4.1.zh-CN.md)
- [开发接续说明](docs/HANDOFF.md)

局部测试不能换算整局 FPS，也没有证明所有卡顿、回弹、加载慢彻底消失。完整游戏/联机 A/B 仍待验证。未知代码与竞争补丁继续保守回退。

工坊沿用条目 [3822098302](https://steamcommunity.com/sharedfiles/filedetails/?id=3822098302)，两种语言标题保持不变。公开源码不包含游戏 DLL、存档或私人迁移材料。
