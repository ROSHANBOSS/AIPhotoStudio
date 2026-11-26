# 版本记录 / Changelog

## 0.5.0 个人修图预览版

本地基线验收日期为 2026-11-26。本次 GitHub 首发面向 Windows x64，以该基线整理为源码预览版。

- 首次启动选择简体中文或 English，设置里可切换或设为每次询问。切换后照片和编辑历史保留。
- 照片可从图库移除并恢复。裁切和画笔草稿可接着修。合照可按人调整，蒙版可独立调节强度。
- 导出支持指定宽高、JPEG 体积限制和局部细节预览。手动或 AI 操作都写入同一份参数历史。
- 本地 0.5.0 基线在一台 Windows 电脑通过 134 组检查，其中 18 个实拍 RAW 文件走通导入和编辑。公开源码版单独验证了本地离线依赖构建、中英文工作区、手动曝光、中文 AI 提案和 JPEG 导出。两组验证对应不同构建。

**源码预览版的变化。** 内置示例改为合成湖景。默认使用当前 Windows 用户的应用数据目录。构建支持标准 Node、Python 和显式提供的依赖位置。已有本地稳定版保留其原数据目录。试用公开版时先开新库，如需迁移请先在旧版备份图库，并保留原片。

### 已知限制

基础编辑可用，复杂指令、人像边缘和部分交互仍在完善。RAW 支持按机型与编码模式判断，高像素文件首次打开仍需等待。HEIC 需要系统解码器。新的低配置电脑、未覆盖的设备和收费云服务仍需独立测试。

## English summary

The local 0.5.0 baseline was verified on 2026-11-26. It adds persistent language choice, recoverable library removal, resumable edit drafts, per-person adjustments and more precise exports. The baseline passed 134 checks on one Windows computer, including import and edit tests on 18 camera files.

This source preview replaces the bundled sample with a synthetic landscape and keeps app data under the current Windows user. Its offline build, Chinese and English workspace, manual exposure, a local proposal and JPEG export were checked separately. AI interpretation, difficult portrait edges, untested camera formats, low-memory PCs and paid provider endpoints still need further testing.
