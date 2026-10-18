# 可选规则移植

通用功能不需要执行这里的脚本。这里只提供经过完整源文件 SHA256 审计的 Mudraptor IsHuman 与 TSM 增益规则原生移植。它会修改明确指定的其他模组 Lua 文件，并保留字节一致的原文备份。不同源版本或已有修改会拒绝覆盖；仅在已审计组合使用，新增相同方法的其他补丁后应撤回并重新核验。

关闭全部游戏/服务器进程，在本目录打开 PowerShell，明确传入当前启用的包根目录：

```powershell
.\Install-KnownModFixes.ps1 -PackageDir '你的 Mudraptor 包目录','你的 TSM 包目录'
```

玩家及真正服务器使用一致改动并完整重启。原生入口不可用时桥接使用原 Lua 规则。

撤回：

```powershell
.\Install-KnownModFixes.ps1 -PackageDir '你的 Mudraptor 包目录','你的 TSM 包目录' -Restore
```

脚本拒绝覆盖安装后又发生改动的 Lua 文件。备份位于原文件旁 `.ReboundGuard040.original`。工坊更新这些其他模组后可能覆盖修改，须重新核验；不要靠改哈希强行放行未知代码。
