# prompt-rebuild — 纯提示词重建包

这个目录是「RollerCAN 钓鱼模拟器」的**纯提示词风格重建项目**：不含任何代码，
只保留重建所需的不可再生材料。

## 内容

| 路径 | 作用 |
|---|---|
| `ACTIVATE.md` | **一键激活提示词**：全文粘贴给 Kimi Code 即可从零重建整个项目 |
| `assets/fish/` | 7 张像素鱼 PNG（去背 RGBA）——固件精灵与 Unity 素材的唯一来源 |
| `skills/flux-klein/` | 本地文生图 skill（要重新生成/新增鱼图时用） |
| `skills/unity-cli-pipeline/` | Unity CLI/批处理操作与排障 skill |
| `docs/` | 5 份施工计划文档（协议、参数、全部踩坑记录——重建前先通读） |

## 用法

1. 新建一个空目录作为工作区。
2. 把 `prompt-rebuild/` 整个拷进工作区。
3. 打开 Kimi Code，把 `ACTIVATE.md` **全文**粘贴为第一条消息。
4. Agent 会按规格重建：两个固件工程、RollerFlasher 烧录器、Unity 工程、
   Blender 模型脚本，并按完成判据逐项编译验证；烧录与 Play 由你执行。

## 说明

- 代码刻意不包含：ACTIVATE.md 里的规格 + docs/ 里的施工记录已足以让 Agent
  重新实现全部代码；素材（鱼图）无法靠提示词再生，故随包携带。
- 仅 macOS：整条工具链（RollerFlasher、Blender Steam 版、Unity 验证环境）
  只在 macOS 上有效。
