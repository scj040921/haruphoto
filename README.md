# 📸 haruphoto

<div align="center">

<img src="docs/logo.png" width="128" alt="haruphoto logo"/>

</div>

> 一款轻量、优雅的 Windows 照片管理应用  
> 基于 WinUI3 + .NET 8 构建，原生 Windows 体验

<div align="center">

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue?style=flat-square)
![Framework](https://img.shields.io/badge/framework-WinUI3%20%2B%20.NET%208-blueviolet?style=flat-square)
![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)
![Release](https://img.shields.io/github/v/release/scj040921/haruphoto?style=flat-square&color=black)

</div>

---

## ✨ 功能一览

<table>
<tr>
<td width="50%">

### 🖼️ 照片管理
- 📂 一键导入文件夹（递归扫描子目录）
- 🔍 模糊搜索文件名
- 📅 按添加时间 / 文件名 / 评分排序
- 📄 分页浏览（每页 100 张，流畅滚动）

</td>
<td width="50%">

### ❤️ 收藏 & 评分
- ⭐ 1-5 星评分系统
- ❤️ 一键收藏 / 取消收藏
- 🔖 仅查看收藏照片
- 📊 底部状态栏实时统计

</td>
</tr>
<tr>
<td>

### 🏷️ 分类系统
- 🏷️ 创建自定义分类（旅行、家人、美食…）
- 📋 预览面板快速分配分类
- 🔎 按分类筛选浏览
- ✏️ 重命名 / 删除分类（自动更新关联）

</td>
<td>

### 🔍 全屏预览
- 🖼️ 大图预览（限宽 1600px 解码，省内存）
- ⬅️➡️ 键盘左右切换，Esc 关闭
- 📋 复制图片到剪贴板
- 📁 在资源管理器中定位文件

</td>
</tr>
<tr>
<td>

### ⚙️ 智能扫描
- 🔄 自动监控已导入文件夹
- 🆕 定时检测新增图片并自动添加
- ⏱️ 可配置扫描间隔（1-60 分钟）
- 💾 设置持久化，重启不丢失

</td>
<td>

### 🎨 现代 UI
- 🌙 深色模式支持
- 🎨 低饱和度配色方案
- 📐 Fluent Design 圆角卡片
- 💻 自适应 NavigationView 布局

</td>
</tr>
</table>

---

## 🚀 快速开始

### 方式一：下载发布包（推荐）

1. 前往 [Releases](https://github.com/scj040921/haruphoto/releases) 下载最新版本
2. 推荐下载 `haruphoto-v1.3.2-Setup.exe`，直接双击并点击「安装」，不再需要运行 `.cmd`、安装 .NET 或开启开发者模式
3. 只想免安装运行时，下载 `*-portable.zip`，解压后双击 `PhotoAlbum.exe`
4. 安装版会创建开始菜单、桌面快捷方式和 Windows 卸载入口

安装版启动后会每 12 小时检查一次 GitHub Release。发现新版本时，可在「设置 → 更新」手动确认下载。更新包会先校验 SHA256，升级失败会自动恢复旧版本。照片文件、图库索引和设置不会被更新过程删除。

> ⚠️ 需要 64 位 Windows 10 1809+ 或 Windows 11；不支持 32 位 Windows。
> ⚠️ 首次运行会自动在桌面创建快捷方式

不要下载页面底部的 **Source code (zip)** 来安装，那是源码，不含可运行的程序。
免安装包必须先「全部解压」，保留所有 DLL、资源和子目录，不能只复制 EXE。
旧版 `.cmd` 安装包无法运行时，改用本页的 Setup.exe；如果 EXE 也被系统或安全软件拦截，请发出完整提示，不要关闭防护或修改系统关联。
安装器尚未代码签名，Windows 可能提示发布者未知；请先确认来自本仓库并与 `SHA256SUMS.txt` 核对。
如果安装失败，会显示具体原因和 `%TEMP%\\haruphoto-install-*.log` 日志位置，发送该日志有助于排查。

### 方式二：从源码构建

```bash
# 克隆仓库
git clone https://github.com/scj040921/haruphoto.git
cd haruphoto

# 确保已安装 .NET 8 SDK
dotnet --version

# 构建
dotnet build PhotoAlbum.csproj -c Release --self-contained true

# 运行
dotnet run -c Release
```

### 方式三：发布独立包

```bash
# 构建 Release
dotnet build -c Release

# 复制输出（自包含，无需安装运行时）
powershell -ExecutionPolicy Bypass -File tools/package.ps1 -AppOutput bin/Release/net8.0-windows10.0.26100.0/win-x64
```

---

## 📐 项目结构

```
PhotoAlbum/
├── App.xaml / App.xaml.cs          # 应用入口 & 主题资源
├── AppSettings.cs                  # 设置持久化（JSON）
├── MainWindow.xaml / .cs           # 主界面 & 全部交互逻辑
├── PhotoItem.cs                    # 照片数据模型（INotifyPropertyChanged）
├── LibraryStore.cs                 # 照片库持久化（JSON）
├── ThumbnailService.cs             # 缩略图管线（缓存 + 异步解码）
├── FolderWatcherService.cs         # 文件夹定时扫描
├── ShortcutService.cs              # 桌面快捷方式创建
├── UpdateService.cs                 # GitHub Release 检查与安全更新启动
├── tools/install.cmd                # 双击安装入口
├── tools/install.ps1                # 非管理员安装脚本
├── tools/update.ps1                 # 下载、校验、替换与回滚
├── tools/uninstall.ps1              # 卸载程序文件（保留照片库数据）
├── installer/haruphoto.iss          # Inno Setup 安装器脚本
├── PhotoAlbum.csproj               # 项目配置
├── app.manifest                    # Windows 兼容性清单
└── Assets/                         # 图标资源
```

---

## 🏗️ 技术架构

```
┌─────────────────────────────────────────────┐
│                 haruphoto                    │
├─────────────────────────────────────────────┤
│  WinUI3 (XAML)  │  Fluent Design Controls   │
├─────────────────┼───────────────────────────┤
│  .NET 8         │  C# / Async-Await         │
├─────────────────┼───────────────────────────┤
│  Windows App    │  StorageFile Pickers       │
│  SDK 1.8        │  BitmapDecoder/Encoder     │
├─────────────────┼───────────────────────────┤
│  Unpackaged     │  WindowsPackageType=None   │
│  Deployment     │  Self-Contained Runtime    │
└─────────────────┴───────────────────────────┘
```

### 关键设计决策

| 决策 | 原因 |
|------|------|
| **Unpackaged 模式** | 无需 MSIX 打包，绿色运行，适合个人工具 |
| **JSON 持久化** | 轻量无依赖，`%LocalAppData%\haruphoto\` |
| **缩略图缓存** | SHA1 哈希键，400px 限宽解码，JPEG 编码 |
| **定时轮询扫描** | `FileSystemWatcher` 在 unpackaged 下不稳定 |
| **无 Mica/Acrylic** | unpackaged 模式下会崩溃，用纯色背景替代 |

---

## 🎯 使用技巧

| 操作 | 方式 |
|------|------|
| 导入照片 | 左下角 `＋ 导入文件夹` |
| 收藏照片 | 卡片右上角 ☆ 按钮 |
| 评分照片 | 预览面板 ⭐ 区域 |
| 分配分类 | 预览面板 → 分类下拉框 |
| 管理分类 | 预览面板 → 分类 → 管理 |
| 搜索 | 工具栏搜索框 |
| 切换主题 | 左下角 ⚙ 设置 |
| 复制图片 | 预览面板 → 📋 复制图片 |
| 键盘快捷键 | 预览中：← → 切换，Esc 关闭 |

---

## 🛠️ 开发环境

- **OS**: Windows 10 1809+ / Windows 11
- **SDK**: .NET 8 SDK
- **框架**: Windows App SDK 1.8
- **IDE**: Visual Studio 2022 / VS Code / Rider

### 已知限制（Unpackaged 模式）

| 不可用功能 | 替代方案 |
|-----------|---------|
| `MicaBackdrop` / `AcrylicBackdrop` | 纯色背景 |
| `Flyout` | `ContentDialog` |
| `TitleBar` 自定义 | 默认标题栏 |
| `PublishSingleFile` | 文件夹发布 |

---

## 📝 更新日志

### v1.3.3 (2026-10-13)
- 新增 Stable / Nightly 通道与公益下载代理开关，更新信息直接读取 GitHub Releases
- 推送源码自动编译；每日北京时间 02:23 有源码变化才发布 Nightly，正式标签发布 Stable
- 发行包记录通道、标签及源代码 commit；代理下载仍强制核对 GitHub 原始 SHA256
- 详见 [更新与发布通道](docs/UPDATE_CHANNELS.md) 与 [版本说明](docs/RELEASE_NOTES_v1.3.3.md)

### v1.3.2 (2026-10-09)
- 设置改为五个分区，支持搜索、独立草稿、保存/取消、分区重置及自定义外观方案
- 新增 2–4 张对比选片、同步缩放/移动、快捷评分与收藏
- 修复胶片条覆盖详情栏、远端缩略图空白；损坏图片明确显示读取失败
- 修复深色侧栏及设置卡片配色、重复动画事件、快速切换与关闭时的动画竞争
- 预览补齐照片序号和损坏文件提示；取色使用小尺寸采样以降低大照片内存开销
- 关闭自动扫描后不再监控新增文件；图库保存保留离线文件的收藏与评分
- 更新支持运行期间定期检查、版本提示与发布说明、可取消下载、完整包及逐文件 SHA256 校验、失败回滚
- GitHub API 无法访问时读取公开更新清单；包未下载且校验完成前不会退出当前应用
- 新增独立 EXE 安装器，不依赖 `.cmd`、Inno Setup 或电脑预装的 .NET
- 程序和安装器均自带 .NET 8，程序包补齐本地 VC++ 运行时
- 安装前逐文件校验 SHA256；失败恢复旧版本，保留照片、索引和设置
- 修复旧 CMD 入口的 UTF-8/PowerShell 5.1 编码问题
- 修复 CI 使用开发机私有 NuGet 路径和代理、以及未上传 EXE 安装器的问题
- 保留 v1.3.1 的瀑布流、预览缩放、动画、取色背景及新版顶栏/底栏

设置中的「图库布局」可切换规整网格/瀑布流并控制动画；「照片预览」可设置取色背景、详情栏和胶片条显示方式。
预览中按 `Ctrl+D` 进入对比选片，按 `Esc` 返回。时间线仍以源文件修改时间为准。
验证范围与仍未覆盖的环境见 [发布测试报告](docs/RELEASE_TEST_REPORT.md)。

### v1.2.0 (2026-08-22)
- 移除批量操作栏中重复的「收藏」按钮
- 将批量「分类」入口改名为「常用操作」
- 修复胶片预览横向滚动条无法拖动的问题
- 增加左侧固定「检查更新」入口
- 完善 GitHub Release 正式版发布与自动更新流程

### v1.1.0 (2026-08-22)
- 🎉 首次发布
- 📸 照片导入 / 浏览 / 搜索 / 排序
- ❤️ 收藏系统 & ⭐ 评分系统
- 🏷️ 分类管理（创建 / 重命名 / 删除）
- 🔍 全屏预览 & 键盘导航
- 🔄 自动扫描新增图片
- 🌙 深色模式
- 💻 桌面快捷方式自动创建

---

## 📄 License

MIT License

---

<div align="center">

**如果觉得有用，请给个 ⭐ Star 支持一下！**

</div>
