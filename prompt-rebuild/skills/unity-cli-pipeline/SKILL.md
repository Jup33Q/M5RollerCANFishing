---
name: unity-cli-pipeline
description: >
  用 Unity CLI + Unity Pipeline 包驱动本机 Unity 编辑器/项目的操作与排障指南。
  当用户要求操作 Unity 项目（改场景、导资产、查状态、截图验证、批处理构建）或
  unity 命令/pipeline/eval 出问题时使用。含本机实测的坑：Pipeline Server 端口误报补丁、
  eval 不触发域重载、离屏渲染不可信须 play 模式截图、3DGS PLY 导入注意事项。
  深度参考：Obsidian「03-Knowledge-Base/Unity/Unity-CLI与Unity-Pipeline使用教程.md」。
tags: ["unity", "unity-cli", "pipeline", "gaussian-splatting", "urp", "debugging"]
---

# Unity CLI + Pipeline 操作指南（本机实测版）

## 环境现状（2026-08-11）

- CLI：`~/.unity/bin/unity`，版本 `1.0.0-beta.3`（PATH 由 `~/.unity/env` 注入）。
- Pipeline 包：`com.unity.pipeline@0.4.0-exp.1`，Demo01 里有**打过补丁的嵌入副本**（`Packages/com.unity.pipeline`）：Mono HttpListener 不认 `http://+:` 前缀，已改 `http://*:`。官方新版发布后应删除副本换回。
- Demo01（`~/Demo01`，Unity 6000.5.7f1，URP）：嵌入了 `org.nesnausk.gaussian-splatting`（GaussianComposite shader 打了 NaN 保护补丁）；URP Renderer 已加 `GaussianSplatURPFeature`。

## 操作流程

1. **先探测活 Editor**：`unity pipeline list`（看 Server Reachable / 端口）。有活 Editor 就驱动它，不要手改 `.unity`/`.asset` YAML。
2. **eval 执行 C#**：
   - 取值必须 `return`：`unity command eval 'return UnityEngine.Application.unityVersion;'`（裸表达式编译失败）；
   - 语句带分号；复杂逻辑写 `/tmp/xxx.cs` 用 `unity command eval_file`；
   - `GameObject.Find` 找不到 inactive 对象 → 用 `EditorSceneManager.GetActiveScene().GetRootGameObjects()` 遍历；
   - 大量输出写文件（`System.IO.File.WriteAllText`）再读，不靠返回值。
3. **结构性改动后强制域重载**：eval 是内存编译，不触发 domain reload。改 URP Renderer Feature / shader / asmdef 后必须：
   `unity command eval 'UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();'`
4. **验证渲染一律用 play 模式真截图**（离屏 `Camera.Render()` 路径与真实 Game 视图不一致，实测踩过坑）：
   ```bash
   unity command eval 'UnityEditor.EditorApplication.EnterPlaymode();'; sleep 8
   unity command eval 'UnityEngine.ScreenCapture.CaptureScreenshot("/tmp/shot.png", 1);'; sleep 5
   unity command eval 'UnityEditor.EditorApplication.ExitPlaymode();'
   ```
   差分测试：关 Feature / 逐个隐藏对象，每个组合截一张对比。
5. **查编辑器日志**：GUI 编辑器日志可能在 `<项目>/Logs/Editor.log`（`~/Library/Logs/Unity/Editor.log` 会被多实例顶掉）→ `lsof -p <pid> | grep Editor.log` 定位。

## 常见坑速查

| 症状 | 根因与修复 |
|---|---|
| `unknown command 'pipeline'` | CLI 太旧（< 0.1.0-beta.5 无 pipeline 组）→ `unity upgrade -y` |
| Pipeline Server「No available ports 7800-7849」 | Mono HttpListener `+` 前缀 bug → 嵌入包改 `http://*:`（见上方环境现状） |
| 改了配置不生效 | eval 无域重载 → `RequestScriptCompilation()` |
| splat 在 URP 完全不显示 | Renderer 资产没加 `GaussianSplatURPFeature`（RenderGraph only） |
| 画面被巨大黑盘/黑块盖住 | PLY 离群高斯点 → 导入前过滤 `exp(scale)>0.2m` 或 `opacity<-10` 的顶点 |
| splat 模型倒立 | PLY 坐标约定 → X 轴转 180° |
| splat 近看糊 | 导入质量默认 Medium 有损 → VeryHigh 重导；另查 Game 视图 Scale 是否被放大（5x 缩放=位图放大，重置 `m_ZoomArea.m_Scale=(1,1)`） |
| LCC SDK 导 PLY 报 DllNotFound | plyconverter/ClipConverter 是 Windows-only 原生 DLL，macOS 只支持 .lcc；Unity 6 还需插件 meta 加 `validateReferences: 0`（VRModule 已移除） |
| 批处理报"another Unity instance" | 同项目已有 Editor 开着 → 关掉或 `unity projects close` |

## 参考

- 完整教程与版本迁移机制：Obsidian `03-Knowledge-Base/Unity/Unity-CLI与Unity-Pipeline使用教程.md`（含「十三、本机实测经验」）
- 官方命令参考： https://github.com/Unity-Technologies/skills （`skills/unity-cli`）
