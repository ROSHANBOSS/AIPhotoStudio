# 构建依赖

JavaScript 依赖由 package.json 和 package-lock.json 固定版本。native-dependencies.lock.json 记录 WebView2 DLL 与离线 npm 树的校验值。人物模型、WASM 运行时、RAW DLL 及对应上游发行包在恢复时核对固定散列。构建不会读取其他软件的配置目录。

Build tools, runtime requirements, download sources and notices are documented in [Build](build.md) and [Third-party notices](../THIRD_PARTY_NOTICES.md).
