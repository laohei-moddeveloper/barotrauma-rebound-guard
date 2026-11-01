# 2026-11-01 工坊资料同步状态

现有条目：https://steamcommunity.com/sharedfiles/filedetails/?id=3822098302

云端已完成默认标题“联机性能优化”、精简默认简介、公共封面更新。简介按 LF/CRLF 归一化后与 workshop/description.bbcode.txt 一致；线上封面与 workshop/preview-20261101.jpg 的 SHA256 一致：f391450176b731bd5b790437715ce5e984694915a3c28d8d47ec07765b8a4745。没有传 contentfolder，没有重发模组内容。

简体中文本地化标题与简介仍为旧值。SteamCMD 可更新默认资料，但此次 language=schinese 未覆盖原有本地化内容；公开 schinese 页面回读证实未更新，不能以默认接口成功代替验收。

本地 Codex 接续：保护未提交改动，fetch 本分支 codex/workshop-title-20261101，取 workshop/title.txt 和 workshop/description.bbcode.txt；当前新封面已在线，无需再传。通过已登录且拥有条目的 Steam 客户端，以 app 602960 的官方 UGC API，StartItemUpdate(602960,3822098302) 后 SetItemUpdateLanguage('schinese')、SetItemTitle、SetItemDescription、SubmitItemUpdate。不要调用 SetItemContent，不要发布游戏 DLL、修改存档或启动游戏。可复用 workshop/Update-Localizations.ps1 的拥有者校验和官方 SDK 调用，但只更新 schinese，保留其他语言资料。最后回读实际 schinese 标题和简介再报告成功。不得创建新条目。

此分支只含用户请求的标题、简介、封面及操作记录，未合并 main，游戏运行逻辑保持现有 0.4.1。
