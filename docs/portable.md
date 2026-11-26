# 下载免安装版 / Download the portable app

[升级时保留草稿](#升级时保留草稿) · [Download in English](#english) · [Keep your drafts when upgrading](#keep-your-drafts-when-upgrading)

**Windows 64 位个人用户。** 打开下方发布页，在 **Assets** 中选择 **AIPhotoStudio-0.5.0-windows-x64-portable.zip**。[打开 0.5.0 下载页](https://github.com/ROSHANBOSS/AIPhotoStudio/releases/tag/v0.5.0) · [直接下载 Windows 压缩包](https://github.com/ROSHANBOSS/AIPhotoStudio/releases/download/v0.5.0/AIPhotoStudio-0.5.0-windows-x64-portable.zip)

1. 下载完成后，右键 ZIP 选择「全部提取」，解压到一个可写的文件夹。
2. 打开解压后的文件夹，双击 **AIPhotoStudio.exe**。旁边的 DLL 和各个子文件夹需要放在一起。无需安装本软件、Node 或 Python。
3. 选择简体中文或 English，点「打开示例」试修湖景，然后点「导出照片」把成品存为新文件。[四步图文教程](quick-start.md)
4. 绿色 **Code → Download ZIP** 和发布页的 **Source code (zip)** 都是开发者用的源码。下载源码后请按[构建说明](build.md)编译。

运行环境为 Windows x64、.NET Framework 4.8 和 Microsoft Edge WebView2 Runtime。RAW 解码还需 Visual C++ v14 x64 运行库，HEIC 解码取决于本机 WIC 支持。遇到 SmartScreen 提示时，可先核对发布页提供的 SHA-256，并按电脑管理员的策略决定是否运行。

## 升级时保留草稿

**先在旧版备份图库，并单独保留原片。** 0.5.0 把已写入的裁切、手绘蒙版和隐私遮挡草稿存入图库，已应用的调整也有编辑历史。尚未发送的 AI 输入、待确认的 AI 提案和仍在拖动的调整不属于这三类草稿。

1. **在旧版备份。** 打开「图库 → 图库与空间 → 备份整库」，将 `.aiplibrary` 保存到程序文件夹以外的位置。它包含草稿与编辑记录；原始照片请另行复制。备份里可能含文件路径和人物选区，请自己保存。
2. **同一台电脑换程序。** 关闭旧版，将新版完整解压到新文件夹，再启动。沿用同一 Windows 账户及同一份兼容图库。0.5.0 的默认图库位于 `%LOCALAPPDATA%\AIPhotoStudio\Preview\Data\0.5\stable`。如果原来用 `--data-root` 自选根目录，继续使用原参数。
3. **换电脑或需要迁移。** 带上图库备份和原片。在新版打开「图库 → 图库与空间 → 从备份恢复」，选中 `.aiplibrary`，为恢复的图库指定一个尚不存在的新目录，再点「打开已恢复的图库」。原片位置变了，在「图库 → 找回原片」中选择现在存放照片的文件夹。
4. **核对后再清理。** 打开一张旧照片，继续草稿、查看历史并另存一张成品。若看到空图库，先核对账户和图库位置，保留原库及备份。回退旧版时，从升级前备份恢复到独立目录。

**验证范围。** 已用公开 0.5.0 的两份程序实测：从另一文件夹启动并打开同一图库，以及从整库备份恢复到新目录，两种情况下均找回三种草稿和已提交的曝光调整。跨版本兼容情况请查看目标版本的发布说明。

## 可选的人物模型

手动调色、裁切、手绘蒙版、离线指令和 JPEG 导出可以直接试用。自动人体、逐脸识别及人物语义蒙版另需 Google 的模型。下面是固定版本的上游直链，总计约 25.9 MB。只在需要这些功能时下载。关闭应用，把文件按表中名称放入解压目录的 `ui/BodyVision/`，再打开应用。下载时请保留扩展名。[模型与运行时许可](../THIRD_PARTY_NOTICES.md)

| 功能 | 下载并保存为 | 字节数 |
| --- | --- | --- |
| 人物和衣物分割 | [selfie_multiclass_256x256.tflite](https://storage.googleapis.com/mediapipe-models/image_segmenter/selfie_multiclass_256x256/float32/1/selfie_multiclass_256x256.tflite) | 16,371,837 |
| 逐脸识别 | [face_landmarker.task](https://storage.googleapis.com/mediapipe-models/face_landmarker/face_landmarker/float16/1/face_landmarker.task) | 3,758,596 |
| 人体识别 | [pose_landmarker_lite.task](https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_lite/float16/1/pose_landmarker_lite.task) | 5,777,746 |

## English

Under **Assets** on the [release page](https://github.com/ROSHANBOSS/AIPhotoStudio/releases/tag/v0.5.0), download **AIPhotoStudio-0.5.0-windows-x64-portable.zip**. Choose **Extract All**, keep the extracted files together and open **AIPhotoStudio.exe**. Select a language and click **Open sample**. To run it you need Windows x64, .NET Framework 4.8 and WebView2 Runtime. No development tools are required. The source archives require a build.

Manual editing, painted masks, offline instructions and JPEG export work without the optional vision weights. For automatic portrait features, close the app, download the three Google files in the table into `ui/BodyVision/` by the exact names shown and reopen the app. Use the [English illustrated guide](quick-start.en.md) to try your first edit.

## Keep your drafts when upgrading

**Back up your library in the old app and keep a separate copy of your original photos.** Version 0.5.0 stores saved crop, painted mask and privacy redaction drafts in the library, along with the history of applied edits. Unsent AI messages, unconfirmed AI proposals and adjustments still being dragged are outside that draft coverage.

1. **Back up in the old version.** Open **Library → Library and storage → Back up entire library**. Save the `.aiplibrary` file outside the application folder. It includes drafts and editing records. Copy your original photos separately. Keep the backup private because it can contain file paths and person selections.
2. **Change app folders on the same PC.** Close the old app, extract the new ZIP into a new folder and launch it. Use the same Windows account and compatible library. The default 0.5.0 library is `%LOCALAPPDATA%\AIPhotoStudio\Preview\Data\0.5\stable`. If you used `--data-root` to choose a library root, keep the same launch argument.
3. **Move to a new PC or migrate a library.** Bring the backup and your originals. In the new app, choose **Library → Library and storage → Restore from backup**, select the `.aiplibrary` file and give the restored library a new folder name that does not already exist. Choose **Open restored library**. If the photos moved, use **Library → Relink originals** and select their current folder.
4. **Check your work.** Open an old photo, resume a draft, inspect its edit history and export a copy before removing the old app. If the library appears empty, check the Windows account and library location first. Keep the original library and backup. To roll back, restore a pre-upgrade backup into a separate folder for the older version.

**What we tested.** Two copies of the public 0.5.0 app in different folders recovered all three draft types and an applied exposure edit, both when opening the same library and after restoring a backup to a new location. Check the target version’s release notes for tests between different versions.
