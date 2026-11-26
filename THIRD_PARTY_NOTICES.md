# Third-party software and media

These terms apply to their respective components. This release contains application source, license notices, an original synthetic landscape and documentation images. The local build restores its runtime dependencies separately.

| Component and source | Version and use | Distribution and notices |
| --- | --- | --- |
| [React](https://registry.npmjs.org/react), [React DOM](https://registry.npmjs.org/react-dom), [Scheduler](https://registry.npmjs.org/scheduler) | 19.3.0, 19.3.0, 0.28.0. UI runtime bundled into the built application. | MIT. [React](licenses/react-LICENSE.txt), [React DOM](licenses/react-dom-LICENSE.txt), [Scheduler](licenses/scheduler-LICENSE.txt). |
| [esbuild](https://github.com/evanw/esbuild), [TypeScript](https://github.com/microsoft/TypeScript), [DefinitelyTyped](https://github.com/DefinitelyTyped/DefinitelyTyped), [csstype](https://github.com/frenic/csstype) | Windows esbuild 0.28.1, TypeScript 5.9.3, React declarations 19.3.0 and csstype 3.2.3. Build and type-check dependencies restored by npm. | TypeScript is Apache-2.0. The other listed packages are MIT. Exact npm sources and integrity hashes are pinned in the source lock file. |
| [Microsoft WebView2 SDK](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.2535.41) | 1.0.2535.41. Two managed assemblies and the Windows x64 loader. Uses the WebView2 Runtime installed on the computer. | [SDK notice](licenses/WebView2-notice.txt). Windows, .NET Framework, WIC and system SQLite are supplied by the operating system. |
| [LibRaw](https://www.libraw.org/download) | 0.22.3, official unmodified Windows x64 DLL, loaded by a separate camera worker. | This build uses the upstream CDDL 1.0 option. Preserve the corresponding complete upstream source distributed with the DLL in the local build. [Copyright](licenses/LibRaw-COPYRIGHT.txt), [CDDL](licenses/LibRaw-LICENSE.CDDL.txt), [additional upstream LGPL text](licenses/LibRaw-LICENSE.LGPL.txt). |
| [MediaPipe Tasks Vision](https://github.com/google-ai-edge/mediapipe/tree/v0.10.21) | Tasks Vision 0.10.21 with CPU/WASM. Google Pose Landmarker Lite, Face Landmarker and Selfie Multiclass 256×256, each revision 1. Used for local portrait, body and segmentation controls. | Apache-2.0 per the recorded upstream notices. The source tree includes notices and pinned download references. The model weights and runtime binaries are restored separately for a local build. [License](licenses/MediaPipe-Apache-2.0.txt), [pose](licenses/BodyVision-notice.txt), [face](licenses/PortraitVision-notice.txt), [segmentation](licenses/MaskVision-notice.txt). |
| Interface fonts | Requests Inter, Segoe UI or Microsoft YaHei families available on the user's system, with a sans-serif fallback. | No font files are distributed. |
| Application mark and inline interface icons | The interface renders these graphics from source. | The maintainer confirms that the project created these graphics, with original or AI-assisted work, and has the right to distribute them. The first-party license applies. |
| Stillwater and guide images | Original procedural landscape built with the included sample generator. Screenshots show this synthetic scene in the actual app. | No photographic subject, customer image or third-party sample photograph is included. The earlier US Navy Grace Hopper sample is replaced in this release. |

## 中文说明

构建时恢复的组件、软件随附的声明与系统运行时各自适用上表所列条款。LibRaw 的 DLL 和对应上游源码须一起处理。模型权重单独从固定的上游来源恢复。维护者已确认应用标志和内嵌图标由项目自行或借助 AI 制作，有权随本版授权。项目授权范围见根目录的 LICENSE，示例和教程中的风景均由程序绘制。
