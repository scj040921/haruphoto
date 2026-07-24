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
![Release](https://img.shields.io/badge/release-v1.0.0-orange?style=flat-square)

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
2. 解压到任意目录
3. 双击 `PhotoAlbum.exe` 启动

> ⚠️ 需要 Windows 10 1809+ 或 Windows 11  
> ⚠️ 首次运行会自动在桌面创建快捷方式

### 方式二：从源码构建

```bash
# 克隆仓库
git clone https://github.com/scj040921/haruphoto.git
cd haruphoto

# 确保已安装 .NET 8 SDK
dotnet --version

# 构建
dotnet build -c Release

# 运行
dotnet run -c Release
```

### 方式三：发布独立包

```bash
# 构建 Release
dotnet build -c Release

# 复制输出（自包含，无需安装运行时）
cp -r bin/Release/net8.0-windows10.0.26100.0/win-x64/ ./publish/
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

### v1.0.0 (2026-07-24)
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
