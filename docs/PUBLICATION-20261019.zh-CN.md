# 2026-10-19 发布记录

用户要求直接整理发布，停止追加优化和测试。构建为 `performance-maintenance-20261019`，模组版本保持 0.4.0。

## GitHub 已完成

- 发布源码提交：`fd8087b6db956481f4172530c0f549ac5bb3e680`。
- 标签：`v0.4.0-performance-maintenance-20261019`。
- [公开实验版 Release](https://github.com/laohei-moddeveloper/barotrauma-rebound-guard/releases/tag/v0.4.0-performance-maintenance-20261019)。不是 draft，标为 prerelease。
- [发布工作流](https://github.com/laohei-moddeveloper/barotrauma-rebound-guard/actions/runs/39190144299)成功。只打包并上传，没有执行游戏或测试。
- 已通过 GitHub API 读回三个上传附件的名称、大小与 digest。

| 附件 | 字节 | GitHub SHA256 |
| --- | ---: | --- |
| `ReboundGuard_0.4.0_performance-maintenance-20261019_experimental.zip` | 994229 | `7a5137a6f2aa603160584f05e0cba12b54f59d925eb21f7a648e5ff93e7bb600` |
| `ReboundGuard_0.4.0_performance-maintenance-20261019_workshop.zip` | 445366 | `67f702cc25ff23434dc10d94358e21fd451ffb3f186f9376e10d2ceda39a47ad` |
| `SHA256-performance-maintenance-20261019.txt` | 266 | `762329db9ee3795648e4a3e4b32e1d60b3b77fdab59100819e978b040587f392` |

源码包包含安装脚本、当前 canonical 源码、公开说明、验证工具，以及独立准备的工坊目录与发布脚本。采用明确文件清单，不复制私人研究、原始日志或旧运行时。附件由 GitHub runner 独立压缩；其 ZIP digest 与云端先前本地压缩包可以不同，以上以公开附件读回值为准。

## 工坊已完成本机上传与读回

2026-10-19 10:33（Asia/Hong_Kong），本机 Windows 已更新原条目 [3822098302](https://steamcommunity.com/sharedfiles/filedetails/?id=3822098302)。先下载上表 GitHub 源码附件并核对完整 SHA256，再从独立 build 目录解压发布；未使用仓库内历史工坊目录。当前登录 Steam 账号的条目归属与 App 602960 检查通过。

- 内容提交返回 `OK`，无需新协议确认；公开状态，内容大小 **559234 字节**。
- 新封面已更新，读回 URL 见 `workshop/verification.json`。
- `schinese` / `english` 分别提交并通过原生语言查询独立读回，标题和简介完整匹配；见 `workshop/localization-verification.json`。
- 重复 `AddDependency` 返回 false，发布入口因此在语言更新前停止。没有把该结果当成依赖成功：另行执行语言更新后，以 `WithChildren(true)` 查询实际依赖列表，确认 **2559634234** 已存在。见 `workshop/dependency-verification.json`。`publish-state.json` 分别保留添加返回值与实际存在的验证状态。
- 仅调用 Steam SDK 上传和查询；未启动游戏客户端或服务器，未安装模组或改写游戏 DLL。本次发布没有追加游戏测试。

发布内容：

- 原条目 **3822098302**，App **602960**，不会创建新条目。
- 简体中文：**深潜稳帧 Rebound Guard | 联机性能优化**。
- 英文：**Rebound Guard | Multiplayer Performance**。
- 两种简介都附 GitHub 源码链接，包含联机性能、卡顿、FPS、LuaCs 等相关关键词；按 Steam 语言字段分别设置。
- 新中英封面：`workshop/assets/cover-multiplayer.png`；工坊 JPEG 为 383245 字节，小于 1 MiB。
- `Publish-Prepared.ps1` 先检查内容清单与哈希、当前登录账号归属，再上传同一条目、封面和 LuaCs 前置，随后提交并独立读回 `schinese` / `english`。

在登录创作者 Steam 账号的 Windows 上，解压 Release 的 **experimental.zip 源码包**，进入其根目录执行：

```powershell
pwsh -NoProfile -File .\workshop\Publish-Prepared.ps1 -GameDir 'D:\steam\steamapps\common\Barotrauma'
```

路径来自上次本机验证记录；若实际安装目录改变，传入新的目录。保持 Steam 登录即可；不启动游戏客户端或服务器，不替换游戏 DLL。不要把另一个仅含工坊内容的 ZIP 当作发布脚本包；也不要使用仓库内历史 `workshop/publish` 副本，它缺少新内容清单，脚本会拒绝上传。

## 验证说明

发布说明与包内 `PUBLIC-PACKAGE.json` 明确记录：基础批次已做 Windows 两端隔离检查，后续尾部维护路径尚待该验证；没有整局联机或渲染收益保证。本次整理发布没有追加测试。
