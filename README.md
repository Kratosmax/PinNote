# PinNote

PinNote 是一款面向 Windows 10 / Windows 11 的轻量桌面便签工具。它使用 WPF 和 .NET 8 构建，支持桌面便签、全局置顶、分级提醒、统一管理和全局快捷键，数据默认只保存在本机。

当前版本：`0.9.0`

> **0.6.0 升级提示：** `0.6.0` 的自动更新会在启动新版更新器之前被临时 ZIP 文件锁阻断，因此无法自动安装 `0.6.1`。请从 [最新 Release](https://github.com/Kratosmax/PinNote/releases/latest) 手动下载并安装一次 `0.9.0`；便签和设置保存在 `%LOCALAPPDATA%\PinNote`，不会因覆盖安装或替换便携包而删除。升级后，后续自动更新恢复正常。

## 0.9.0 本轮更新

- 新增回收站：便签和待办删除后可恢复，默认保留 30 天，并可在设置中自定义保留期限。
- 新增统一搜索与筛选，可跨便签和待办查找，并按提醒、逾期和完成状态筛选。
- 新增提醒中心，集中查看便签和待办的提醒时间与逾期状态。
- 稍后提醒新增 5 分钟、30 分钟、1 小时和明天 09:00 四种预设。
- 支持复制便签和整棵待办；复制的待办会保留层级与提醒，并重置为未完成。
- 托盘中的便签与待办菜单改为对等结构，补齐待办入口，并修复管理页图标和待办文字垂直对齐。

## 下载选择

| 版本 | 是否安装 | 自带 .NET 8 运行时 | 适合人群 |
| --- | --- | --- | --- |
| Full Setup | 是 | 是 | 推荐给普通用户，安装后直接使用 |
| Lite Setup | 是 | 否 | 已安装 .NET 8 Desktop Runtime x64 的用户 |
| Full Portable ZIP | 否 | 是 | 免安装、解压即用 |
| Lite Portable ZIP | 否 | 否 | 体积最小，需自备 .NET 8 Desktop Runtime x64 |

从 [最新 Release](https://github.com/Kratosmax/PinNote/releases/latest) 下载。Setup 默认安装到 `%LOCALAPPDATA%\Programs\PinNote`；Portable ZIP 应先完整解压，再运行 `PinNote.exe`。Full 与 Lite 功能相同，只是运行时携带方式不同。

## 功能概览

- 多便签与基础富文本：粗体、斜体、下划线、Markdown 编辑/渲染、固定文字色和 3 个自定义常用色。
- 两种固定方式：桌面模式与始终置顶。
- 四级提醒：弱提醒、普通提醒、强提醒、超强提醒。
- 提醒时间可精确到秒，悬浮在提醒强度上可查看实际提醒动作。
- 逾期状态、提醒中心，以及 5 分钟、30 分钟、1 小时、明天 09:00 的稍后提醒预设。
- 统一管理页面：便签、待办、设置同级切换；支持跨类型搜索、状态筛选、分组、批量管理、提醒中心和设置分类定位。
- 单实例运行：重复启动时激活已有管理页面，不创建重复进程。
- 自动保存便签内容、窗口位置、尺寸、分组和显示状态。
- 回收站默认保留 30 天，可恢复或永久删除便签和待办，并可在设置中调整期限。
- 支持复制便签和整棵待办；待办副本保留层级与提醒，但重置完成状态。
- 可自定义全局快捷键，也可以分别关闭。
- Windows 11 22621+ 系统背景材质；Windows 10、较早系统或 DWM 调用失败时使用清晰的不透明回退。
- 空闲时没有键盘轮询；全局快捷键使用系统 `RegisterHotKey`。
- 签名自动更新：低频后台检查、手动检查、跳过版本和失败回滚。
- 双层更新代理：GitHub URL 前缀线路和可选 HTTP 网络代理。

## 使用教程

### 第一次启动

运行 `PinNote.exe` 后会创建一张新便签。程序关闭便签窗口后仍驻留在系统托盘；托盘右键菜单提供对等的“便签”和“待办”子菜单，可分别新建、打开管理页，以及显示或隐藏全部对应窗口。需要彻底退出时选择“退出”。

用户数据默认保存在：

```text
%LOCALAPPDATA%\PinNote\notes.json
```

同目录下的 `notes.json.bak` 是最近一次备份。升级或替换程序文件不会主动删除此目录。

### 自动更新

正式安装包和便携包会在启动 15 秒后检查一次更新，此后每 24 小时检查一次；系统定时器等待期间不会轮询或持续占用 CPU。可以在托盘菜单的“设置”中关闭自动检查，或随时点击“检查更新”。Full 和 Lite 使用独立更新通道，不会在更新时互相转换。

发现新版本后可以立即更新、稍后处理或跳过当前版本。立即更新会依次执行：

1. 下载签名清单并验证内置 RSA 公钥。
2. 下载 ZIP，限制最大 200 MB 和连续无数据时间。
3. 验证 SHA-256、文件数量、解压大小、路径、产品通道和包内程序集版本。
4. 保存便签，启动外部更新器，退出主程序。
5. 暂存替换程序文件；失败时恢复已替换文件，成功后重新启动 PinNote。

`0.6.1` 修复了下载完成后临时 ZIP 仍被写入流占用、导致校验阶段报“文件正由另一进程使用”的问题。下载写入现在会先关闭文件句柄，再执行校验和原子改名；失败线路也会清理临时文件后再重试。由于错误发生在 `0.6.0` 启动新版更新器之前，`0.6.0` 必须手动安装一次 `0.6.1`。

更新缓存和最多两个轮转日志位于：

```text
%LOCALAPPDATA%\PinNote\updates
%LOCALAPPDATA%\PinNote\update.log
%LOCALAPPDATA%\PinNote\update.log.old
```

从源码目录直接运行、缺少 `pinnote-install.json` 或更新器文件时不会就地覆盖，只提供 Release 下载入口。

#### 网络代理

在“设置 → 网络与更新”中可以配置两类相互独立的线路：

- GitHub URL 前缀线路：按优先级 10 到 1 依次尝试，0 表示禁用；同优先级保持列表顺序。GitHub 直连不可删除，但可以设为 0。
- HTTP 网络代理：接受 `http://host:port`，暂不支持账号密码、HTTPS 或 SOCKS 代理。

“检查更新”会使用设置页当前尚未保存的线路测试；保存后，后台检查、手动检查和更新包下载共用同一策略。前缀线路只改写原始 `github.com` 地址，不会转发其他域名。无论使用哪条线路，清单签名、SHA-256、版本、通道和包结构校验都不会跳过。

GitHub URL 前缀服务会看到完整 GitHub 下载 URL。建议优先使用可信的 HTTPS 前缀；HTTP 前缀或 HTTP 网络代理不能隐藏下载内容和流量元数据。

卸载 Setup 不会删除 `%LOCALAPPDATA%\PinNote` 中的便签数据。Portable 版本也把用户数据保存在该目录，而不是 ZIP 解压目录。

### 编辑和摆放便签

- 使用标题栏中的六点拖动柄移动便签。
- 标题栏的图钉按钮可在“桌面模式”和“始终置顶”之间切换。
- 隐藏按钮只隐藏当前便签，不删除数据。
- 便签内容、位置和尺寸会自动保存。

### 设置提醒

点击便签标题栏中的时钟按钮，可以选择快捷时间、指定日期时间和提醒等级：

| 等级 | 行为 |
| --- | --- |
| 弱提醒 | 便签边框柔和闪动 |
| 普通提醒 | 便签闪动并发送 Windows 通知 |
| 强提醒 | 显示前台提醒窗口，但不主动抢夺输入焦点 |
| 超强提醒 | 尝试抢占焦点并持续闪动，直到用户处理 |

已超过提醒时间的便签会保留柔和的警告色边框。

### 统一管理与回收站

双击托盘图标，或使用管理页面快捷键，可以打开统一管理页面。在这里可以：

- 跨便签和待办搜索标题与正文，并按提醒、逾期和完成状态筛选。
- 新建、打开、复制或删除便签与待办。
- 创建、重命名和删除分组，修改项目所属分组。
- 隐藏或重新显示便签，在提醒中心集中查看提醒与逾期状态。
- 从回收站恢复项目，或永久删除不再需要的数据。

普通删除会移入回收站。程序启动时会清理超过保留期限的项目；默认保留 30 天，可在“设置 → 常规”中修改。

### 全局快捷键

默认快捷键：

| 功能 | 默认组合 |
| --- | --- |
| 新建便签 | `Ctrl+Shift+N` |
| 打开管理页面 | `Ctrl+Shift+B` |

在托盘菜单的“设置”中聚焦快捷键输入框并按下新组合即可修改。两个快捷键可以分别关闭；如果组合重复或已被其他程序占用，设置不会覆盖当前有效配置。

## 自行编译

### 环境要求

- Windows 10 或 Windows 11（x64）。
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。
- [Inno Setup 6](https://jrsoftware.org/isdl.php)，仅生成四种 Release 包时需要。
- Git；Visual Studio 不是必需项。

### 克隆和构建

```powershell
git clone https://github.com/Kratosmax/PinNote.git
cd PinNote
dotnet build-server shutdown
dotnet build PinNote.sln --configuration Release --disable-build-servers --maxcpucount:1
```

可运行文件会生成到：

```text
temp/build/PinNote/Release/net8.0-windows/PinNote.exe
```

### 运行测试

```powershell
$env:PINNOTE_TEST_TEMP = "$PWD/temp/tests"
dotnet run --project tests/PinNote.SmokeTests/PinNote.SmokeTests.csproj --configuration Release --no-build
```

测试覆盖提醒状态机、数据克隆、旧数据归一化、分组和隐藏状态、窗口几何信息、快捷键设置、代理规范化与稳定路由、JSON 原子保存/备份恢复，以及清单签名、防篡改、不可 Seek 流、真实 ZIP 校验、安装和回滚。

### 本地运行

```powershell
dotnet run --project src/PinNote/PinNote.csproj --configuration Release
```

开发或自动化验证时应使用隔离的数据目录，避免读写正式便签：

```powershell
$env:PINNOTE_DATA_DIR = "$PWD/temp/dev-data"
dotnet run --project src/PinNote/PinNote.csproj --configuration Release
```

### 生成四种候选包

发布脚本会构建项目、运行测试，并生成 Full/Lite 的 Setup 与 Portable ZIP。提供私钥时还会生成 Lite、Full 两条通道的签名更新清单。私钥必须放在被 Git 忽略的 `temp` 目录或其他受控位置：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Build-Release.ps1 `
  -PrivateKeyPath .\temp\signing\pinnote-update-private.pem
```

产物目录：

```text
temp/release/v0.9.0/
```

独立复核真实 ZIP 和签名清单：

```powershell
dotnet run --project tools/PinNote.ReleaseTool/PinNote.ReleaseTool.csproj `
  --configuration Release --no-build -- verify `
  --channel portable-framework-dependent `
  --manifest temp/release/v0.9.0/update.json `
  --package temp/release/v0.9.0/PinNote-0.9.0-Lite-Portable.zip

dotnet run --project tools/PinNote.ReleaseTool/PinNote.ReleaseTool.csproj `
  --configuration Release --no-build -- verify `
  --channel portable-self-contained `
  --manifest temp/release/v0.9.0/update-full.json `
  --package temp/release/v0.9.0/PinNote-0.9.0-Full-Portable.zip
```

### GitHub 发布流程

`.github/workflows/release.yml` 在推送 `v*.*.*` 标签时运行，并强制标签与 `Directory.Build.props` 的版本一致。仓库必须预先配置 Actions Secret `PINNOTE_UPDATE_SIGNING_KEY`，内容为与源码公钥匹配的 PEM 私钥。工作流只授予 `contents: write`，使用临时文件完成签名并在 `finally` 删除私钥。

发布属于独立授权动作。确认候选包、版本和发布说明后，再创建并推送标签；不要覆盖已公开的同版本资产。GitHub Release 正文必须使用只含当前版本变更的 `RELEASE_NOTES_CURRENT.md`；历史 `RELEASE_NOTES.md` 只用于仓库内查阅，不得直接作为 Release 正文。

## 项目结构

```text
src/PinNote/                 WPF 界面、窗口生命周期和 Windows 平台能力
src/PinNote.Core/            数据模型、提醒状态机和 JSON 存储
src/PinNote.Updater/         等待主程序退出、复核签名、替换、回滚和重启
tests/PinNote.SmokeTests/    无第三方测试框架的核心冒烟测试
tools/PinNote.ReleaseTool/   包元数据、签名清单生成与独立验证
scripts/Build-Release.ps1    本地与 CI 共用的候选包构建脚本
installer/PinNote.iss        Full/Lite 当前用户安装器
temp/                        构建、发布和 UI 验证产物（默认不提交）
Directory.Build.props        唯一版本来源、统一编译规则与 temp 输出路径
```

关键入口：

- `src/PinNote/App.xaml.cs`：单实例、托盘、窗口、保存和提醒编排。
- `src/PinNote/Services/GlobalHotkeyService.cs`：系统级快捷键注册。
- `src/PinNote.Core/Storage/JsonNoteStore.cs`：原子写入和备份恢复。
- `src/PinNote.Core/Updates/`：清单验签、包校验和事务式安装。
- `src/PinNote.Core/Models/UpdateNetworkSettings.cs`：代理地址校验、去重和旧配置默认值。
- `src/PinNote/Services/UpdateClient.cs`：联网检查、受限下载和更新器交接。
- `src/PinNote/Windows/NoteWindow.xaml`：便签界面。
- `src/PinNote/Windows/ManagerWindow.xaml`：统一管理页面。

## 使用 AI 继续开发

可以让 Codex、Claude Code、GitHub Copilot CLI 或其他代码 Agent 接手。推荐先把下面这段作为任务上下文：

```text
这是一个 Windows 10/11 的 .NET 8 WPF 便签工具。
请先阅读 README.md、Directory.Build.props、src/PinNote/App.xaml.cs、
src/PinNote.Core/Storage/JsonNoteStore.cs 和现有测试，再提出最小改动方案。
保持低 CPU、低内存、单实例、原子保存、旧 JSON 兼容、Win10 回退和更新签名兼容。
所有构建与验证产物放在项目 temp 目录；修改后运行 Release 构建、冒烟测试，
涉及 UI 时还要启动隔离实例并留下真实渲染证据。
不要提交用户 notes.json、日志、截图缓存、bin/obj 或密钥。
```

### AI 开发约束

1. **先查代码证据**：不要根据界面现象猜调用链。
2. **保持轻量**：提醒调度和快捷键不得使用高频轮询；优先使用系统消息和单次定时器。
3. **兼容旧数据**：新增字段必须有默认值，并通过 `Normalize()` 处理历史 JSON。
4. **保持原子保存**：不要绕过 `JsonNoteStore` 的临时文件、替换和 `.bak` 恢复逻辑。
5. **隔离测试数据**：运行 GUI 测试时设置 `PINNOTE_DATA_DIR` 到 `temp` 子目录。
6. **手术式修改**：不要把功能改动扩大成无关重构或引入大型依赖。
7. **提交前验证**：至少完成 Release 构建、冒烟测试、敏感文件检查和 `git diff` 审阅。
8. **保护更新信任链**：不得替换内置公钥而不安排安全迁移；私钥只能进入 GitHub Secret 或本机 `temp`，不得写入源码、日志和 Release。
9. **保持通道一致**：Lite 使用 `portable-framework-dependent`，Full 使用 `portable-self-contained`；清单、ZIP 元数据和已安装标记必须一致，禁止跨通道更新。
10. **保持代理信任边界**：URL 前缀仅允许原始 `github.com`，所有线路继续执行相同的签名、哈希、版本和包结构校验。

### AI 交付清单

```powershell
dotnet build-server shutdown
dotnet build PinNote.sln --configuration Release --disable-build-servers --maxcpucount:1
$env:PINNOTE_TEST_TEMP = "$PWD/temp/tests"
dotnet run --project tests/PinNote.SmokeTests/PinNote.SmokeTests.csproj --configuration Release --no-build
git status --short
git diff --check
```

涉及 UI 的任务还应检查最小窗口、长文本、禁用态、焦点、Windows 10 回退和高 DPI；不能只用编译成功代替视觉验证。

## 当前边界

- 仅支持 Windows 10 / Windows 11。
- 当前没有账号、云同步或多人协作。
- 自动更新支持官方 Lite 和 Full 两条通道，不支持源码构建目录或跨通道更新。
- Setup 和程序文件当前没有 Windows Authenticode 代码签名；更新真实性由 RSA 清单签名与 SHA-256 保证，因此 Windows 可能显示未知发布者提示。
- `0.4.0` 是自动更新协议的首个版本；发布候选必须至少从上一正式版本完成真实升级回归。
