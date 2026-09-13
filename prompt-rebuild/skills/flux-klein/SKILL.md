---
name: flux-klein
description: 本地 FLUX.2-klein（MLX, Apple Silicon）文生图与图像编辑。当用户要求"生成图片/画一张/文生图/txt2img/flux"，或要"编辑图片/改图/风格迁移/抠图/去背景/移除物体"，或要"估计深度/生成深度图/法线图/depth map/normal map"，或为文章、PPT、网页产出配图时使用。通过 flux-klein MCP 的 generate_image / edit_image / sam_edit / depth_map 工具调用，模型常驻内存。
---

# FLUX.2 Klein 本地文生图 + 图像编辑

通过 `flux-klein` MCP server 在本地生成/编辑图片（mflux / MLX）。

## 工具

### generate_image — 文生图（FLUX.2-klein-9B）

```
generate_image(prompt, width=1024, height=1024, steps=4,
               guidance=1.0, seed=-1, quantize=8, output_path?)
```

- **提示词用英文**，主体 + 场景 + 光线 + 风格。
- Klein 是蒸馏模型，**4 步 / guidance 1.0 是最佳点**；细节不够再加到 6–8 步。
- 同一系列图**固定 seed** 保持风格一致。

### edit_image — 指令式图像编辑

```
edit_image(prompt, image_paths=["/abs/in.png"], engine="klein",
           steps=4, guidance=1.0, seed=-1, quantize=8, output_path?)
```

- `engine="klein"`：FLUX.2-klein edit，快（~20s），适合改色、替换物体、改季节等指令编辑；guidance 1.0。**支持多参考图合成**：把多张图传给 `image_paths`，提示词里用 "image 1 / image 2" 引用，如 "Put the character from image 1 into the scene of image 2"。`image_paths[0]` 是主图。
- `engine="telestyle"`：TeleStyleV2 = Qwen-Image-Edit-2511 底座 + 融合了两个 LoRA（风格迁移 LoRA + Lightning 4 步蒸馏 LoRA）。专长是**双图风格迁移**：`image_paths[0]` 放内容图、`image_paths[1]` 放风格图，提示词用
  "Style Transfer the style of Figure 2 to Figure 1, and keep the content and characteristics of Figure 1."
  只传一张图时也可做普通指令编辑。**guidance 固定 1.0**（DMD 蒸馏不用 CFG），4 步即可。
  首次使用需下载约 57GB 权重（mlx-community/TeleStyleV2-Qwen-Image-Edit-2511-bf16）。
- `image_paths` 第一张为主图；两个引擎都支持多图参考。
- 编辑引擎与文生图共享进程但**单槽缓存**：切换 klein↔telestyle 会卸载前一个模型（重新加载需 1–3 分钟）。

### sam_edit — 分割抠图（SAM3 / BiRefNet）

```
sam_edit(image_path="/abs/in.png", op="cutout", engine="sam3",
         object="the red car", output_path?)
```

- `engine="sam3"`（默认）：SAM3 开放词汇分割，按 `object` 文本指定物体。模型 mlx-community/sam3-8bit 已在本地，首次加载 ~10s。
- `engine="birefnet"`：BiRefNet 显著主体抠图，**无需 object 提示词**，自动找主体，边缘质量更好，去背景首选。torch 实现走 MPS，推理 ~3s；首次使用自动下载 ~886MB 权重（ZhengPeng7/BiRefNet）。
- `cutout`：只保留 object 描述的物体 / 显著主体（RGBA 透明背景）
- `remove`：移除该物体（透明洞）
- `remove_background`：保留主体去背景（sam3 下 object 可省略，自动回退 character/person/object）

### depth_map — 单目深度估计（DA3Mono-Large）

```
depth_map(image_path="/abs/in.png", output_prefix?)
```

- 一次产出三个 PNG：`<prefix>_depth.png`（inferno 彩色深度可视化）、`<prefix>_normal.png`（表面法线图）、`<prefix>_depth16.png`（16-bit 灰度原始深度，min-max 归一化，给 RayRelight/3D 管线用）。
- 返回三个路径 + 内联彩色深度图；法线图是结果的 `path` 字段。
- 首次加载 DA3 模型约 10–30s；整图拉伸到 504×504 推理，不保持宽高比裁剪。

## 通用要点

- 返回：图片路径 + 元数据 + base64 PNG（<5MB 时内联）。生成后把路径用 Markdown 链接给用户：`[name.png](绝对路径)`。
- 输出默认目录：`~/Documents/kimi/workspace/flux-klein-studio/outputs/`。
- 首次调用加载模型耗时 1–3 分钟属正常；之后很快。
- 若报 "backend not ready"，运行 `~/Documents/kimi/workspace/flux-klein-studio/scripts/setup_backend.sh`。

## GUI

桌面版：`~/Desktop/FluxKleinStudio.app`（文生图 / 图像编辑 / 深度 三个模式，⌘⏎ 生成；编辑支持 Klein 多图参考、TeleStyle 双图风格迁移、SAM3/BiRefNet 抠图去背景；深度结果自动按原图分组为历史树节点，可一键 RayRelight 实时打光预览）。
