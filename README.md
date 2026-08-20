# PinNote

PinNote 是一款面向 Windows 10 / Windows 11 的轻量桌面便签工具。它使用 WPF 和 .NET 8 构建，支持桌面便签、全局置顶、分级提醒、统一管理和全局快捷键，数据默认只保存在本机。

当前版本：`0.3.0`

## 功能概览

- 多便签与基础富文本：粗体、斜体、下划线、项目符号和文字颜色。
- 两种固定方式：桌面模式与始终置顶。
- 四级提醒：弱提醒、普通提醒、强提醒、超强提醒。
- 逾期状态、稍后提醒、忽略和完成。
- 统一管理页面：新建、搜索、打开、删除、分组、隐藏和恢复。
- 单实例运行：重复启动时激活已有管理页面，不创建重复进程。
- 自动保存便签内容、窗口位置、尺寸、分组和显示状态。
- 可自定义全局快捷键，也可以分别关闭。
- Windows 11 系统背景材质；Windows 10 自动使用可读的兼容效果。
- 空闲时没有键盘轮询；全局快捷键使用系统 `RegisterHotKey`。

## 使用教程

### 第一次启动

运行 `PinNote.exe` 后会创建一张新便签。程序关闭便签窗口后仍驻留在系统托盘；需要彻底退出时，右键托盘图标并选择“退出”。

用户数据默认保存在：

```text
%LOCALAPPDATA%\PinNote\notes.json
```

同目录下的 `notes.json.bak` 是最近一次备份。升级或替换程序文件不会主动删除此目录。

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

### 管理全部便签

双击托盘图标，或使用管理页面快捷键，可以打开统一管理页面。在这里可以：

- 搜索标题和正文。
- 新建、打开或删除便签。
- 创建、重命名和删除分组。
- 修改便签所属分组。
- 隐藏或重新显示便签。

删除操作不可恢复，程序会在执行前请求确认。

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

测试覆盖提醒状态机、数据克隆、旧数据归一化、分组和隐藏状态、窗口几何信息、快捷键设置及 JSON 原子保存/备份恢复。

### 本地运行

```powershell
dotnet run --project src/PinNote/PinNote.csproj --configuration Release
```

开发或自动化验证时应使用隔离的数据目录，避免读写正式便签：

```powershell
$env:PINNOTE_DATA_DIR = "$PWD/temp/dev-data"
dotnet run --project src/PinNote/PinNote.csproj --configuration Release
```

### 生成便携包目录

以下命令生成依赖 .NET 8 Desktop Runtime 的轻量版本：

```powershell
dotnet publish src/PinNote/PinNote.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained false `
  --output temp/publish/PinNote
```

如果目标电脑没有安装 .NET 8 Desktop Runtime，可以将 `--self-contained false` 改为 `--self-contained true`，代价是包体积明显增大。

## 项目结构

```text
src/PinNote/                 WPF 界面、窗口生命周期和 Windows 平台能力
src/PinNote.Core/            数据模型、提醒状态机和 JSON 存储
tests/PinNote.SmokeTests/    无第三方测试框架的核心冒烟测试
temp/                        构建、发布和 UI 验证产物（默认不提交）
Directory.Build.props        统一编译规则与 temp 输出路径
```

关键入口：

- `src/PinNote/App.xaml.cs`：单实例、托盘、窗口、保存和提醒编排。
- `src/PinNote/Services/GlobalHotkeyService.cs`：系统级快捷键注册。
- `src/PinNote.Core/Storage/JsonNoteStore.cs`：原子写入和备份恢复。
- `src/PinNote/Windows/NoteWindow.xaml`：便签界面。
- `src/PinNote/Windows/ManagerWindow.xaml`：统一管理页面。

## 使用 AI 继续开发

可以让 Codex、Claude Code、GitHub Copilot CLI 或其他代码 Agent 接手。推荐先把下面这段作为任务上下文：

```text
这是一个 Windows 10/11 的 .NET 8 WPF 便签工具。
请先阅读 README.md、Directory.Build.props、src/PinNote/App.xaml.cs、
src/PinNote.Core/Storage/JsonNoteStore.cs 和现有测试，再提出最小改动方案。
保持低 CPU、低内存、单实例、原子保存、旧 JSON 兼容和 Win10 回退。
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
- 自动更新仅预留接口，尚未启用联网更新。
- 当前仓库不包含安装器和代码签名流程。
