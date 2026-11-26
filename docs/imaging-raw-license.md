# RAW 组件的分发

Windows x64 本地构建通过独立 CameraRawWorker.exe 调用未修改的 LibRaw 0.22.3。上游原始 DLL 与完整对应源码来自同一官方发行归档。构建时会核对归档及 DLL 的 SHA-256，在 Raw 子目录保留该归档、版权声明及上游两份许可文本。本项目对这个组件采用 CDDL 1.0。Windows 需提供相应的 Visual C++ v14 x64 运行库。[LibRaw 官方发行](https://www.libraw.org/download) · [上游版权](https://github.com/LibRaw/LibRaw/blob/0.22.3/COPYRIGHT)

The local build retains the complete corresponding upstream source archive with the unmodified DLL. Keep the contents of the Raw directory together.
