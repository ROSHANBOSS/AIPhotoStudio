# 下载免安装版 / Download the portable app

**Windows 64 位个人用户。** 打开下方发布页，在 **Assets** 中选择 **AIPhotoStudio-0.5.0-windows-x64-portable.zip**。[打开 0.5.0 下载页](https://github.com/ROSHANBOSS/AIPhotoStudio/releases/tag/v0.5.0) · [直接下载 Windows 压缩包](https://github.com/ROSHANBOSS/AIPhotoStudio/releases/download/v0.5.0/AIPhotoStudio-0.5.0-windows-x64-portable.zip)

1. 下载完成后，右键 ZIP 选择「全部提取」，解压到一个可写的文件夹。
2. 打开解压后的文件夹，双击 **AIPhotoStudio.exe**。旁边的 DLL 和各个子文件夹需要放在一起。无需安装本软件、Node 或 Python。
3. 选择简体中文或 English，点「打开示例」试修湖景，然后点「导出照片」把成品存为新文件。[四步图文教程](quick-start.md)
4. 绿色 **Code → Download ZIP** 和发布页的 **Source code (zip)** 都是开发者用的源码。下载源码后请按[构建说明](build.md)编译。

运行环境为 Windows x64、.NET Framework 4.8 和 Microsoft Edge WebView2 Runtime。RAW 解码还需 Visual C++ v14 x64 运行库，HEIC 解码取决于本机 WIC 支持。遇到 SmartScreen 提示时，可先核对发布页提供的 SHA-256，并按电脑管理员的策略决定是否运行。

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
