# 隐私与问题反馈 / Privacy and feedback

**默认离线。** 图库保存原片引用、调整参数和历史。应用读取原片，导出写入新文件。公开源码版默认将图库、预设和缓存放在当前用户的应用数据目录。想试用独立图库，可以用 `--data-root` 指定自己新建的空目录。

**网络 AI 是可选项。** 用户选择 Provider、确认端点并启用后，软件可发送输入的指令、当前和允许调整的参数、数值历史及亮度统计。程序不会把原片、缩略图、原片路径或 EXIF 字段加入该请求。用户自己写入指令的姓名或其他信息可能随文字发送，云服务也可能计费。选择「本地规则」即可离线操作。保存的服务配置使用 Windows 当前用户的 DPAPI 加密。

**报告问题。** 请提供版本、Windows 版本、照片格式、复现步骤、预期结果和实际结果。截图前先用合成示例复现，检查文件名、图库侧栏、通知、账号及路径。公开讨论仅包含可公开的信息。遇到可能涉及凭据或用户数据的问题，先向仓库维护者询问私下联系方式。

**English.** The default rule-based assistant works offline. A catalog stores file references and editing history locally. Exports create new files. If you enable an external provider, requests can include your instruction, parameters, numerical history and brightness statistics. Image files, thumbnail pixels, original file paths and EXIF fields are not part of those requests. Text you type can itself contain personal information. Provider fees may apply. Saved settings are encrypted with Windows DPAPI for the current user.

For a bug report, include versions, image format, reproducible steps, expected behavior and actual behavior. Use the synthetic sample to reproduce the issue. Keep original photos, raw logs, account details and catalog backups out of public reports.
