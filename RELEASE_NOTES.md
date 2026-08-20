## PinNote 0.4.1

- 新增 Full Setup、Lite Setup、Full Portable ZIP、Lite Portable ZIP 四种分发格式。
- Full 自带 .NET 8 Desktop Runtime；Lite 体积更小，需要系统已安装 .NET 8 Desktop Runtime x64。
- 新增 Full 自包含更新通道，并继续保留 `0.4.0` 使用的 Lite 通道。
- Full 与 Lite 更新包会验证各自的通道标记，拒绝跨通道替换。
- Setup 采用当前用户安装，无需管理员权限；便签数据继续保存在 `%LOCALAPPDATA%\PinNote`。

当前安装包尚未使用 Windows Authenticode 代码签名，Windows 可能显示未知发布者提示。自动更新包仍使用 RSA 签名清单和 SHA-256 校验。
