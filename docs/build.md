# 从源码构建 / Build on Windows

## 环境

使用 Windows x64、.NET Framework 4.8（含 Framework64 编译器和 WPF 程序集）、WebView2 Runtime、Node.js 24.14.x、npm 11.x 和可由 `python` 启动的 Python 3。Windows 的 Visual C++ v14 x64 运行库用于 RAW 工作进程。HEIC 读取取决于本机 WIC 解码器。运行已经构建好的应用只需相应系统运行时。

在仓库根目录打开 PowerShell。首次构建会访问 npm、NuGet、Google 模型托管和 LibRaw 官方站点，恢复本地依赖。将模型放入自己的构建目录即可，无需账户密钥。示例图已经包含在源码中。

```powershell
npm ci --ignore-scripts
npm run bootstrap -- --fetch
npm test
npm run build
.\build\development\AIPhotoStudio.exe
```

首次运行选择界面语言，点「打开示例」应出现一幅湖面帆船图。默认手动修图和离线指令均可用。需要更换数据目录时，可使用 `--data-root` 加一个可写绝对路径。请给试用版选择新的空目录。

## Offline and repeat builds

Start in the repository root. Use the PowerShell sequence above for the first build. npm verifies the lockfile integrity. The bootstrap checks the SDK DLLs and the upstream RAW and MediaPipe assets. To rebuild with verified dependencies already present, omit `--fetch`. A missing or mismatched file stops the build and identifies the component.

For an offline build, supply the pinned npm tree through the process variable `AIPHOTO_NPM_SOURCE`, and the matching SDK folder through `AIPHOTO_WEBVIEW_SDK`. Populate `build/vendor/BodyVision/` with the three pinned model files, `build/vendor/body-downloads/` with the tasks-vision 0.10.21 npm archive, and `build/vendor/raw-downloads/` with the official LibRaw 0.22.3 archive. See [model filenames and hashes](../scripts/body-assets.mjs) and [RAW archive hash](../scripts/raw-stage.py). Set `AIPHOTO_BUILD_PYTHON` only if your Python executable has a different name. Run the bootstrap without `--fetch`, then the test and build steps above.

`npm test` performs type checks, preset composition checks and an English editing-label audit. Personal photos and private test libraries are unnecessary. Build products, restored weights, caches and app data belong outside the files submitted to GitHub.
