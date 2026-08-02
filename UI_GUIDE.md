# 🎨 haruphoto UI 设计规范

> 本规范沉淀 haruphoto 全部视觉/交互决策与平台限制实证结论。
> 所有新功能开发前必须阅读，避免重复踩坑。

## 1. 设计语言

- **风格定位**：SPW 风格亚克力毛玻璃 · 苹果质感 · 低饱和度商业配色
- **配色**：默认强调色 `#5B6EAE`（柔和蓝紫），全局饱和度系数 0.55
- **字体**：MiSans 系统字体（中文界面）
- **主题**：三模式 —— 跟随系统 / 浅色 / 深色（`ThemeMode`: -1/0/1）
  - 所有硬编码材质颜色判断统一走 `IsDark()`（跟随系统时读 `ActualTheme`）
  - 跟随系统模式挂 `ActualThemeChanged` 自动刷新材质
- **圆角**：卡片默认 14px（`CardCornerRadius`，可设置 6/10/14/18）
- **动画**：默认 350ms，可关闭（`AnimationsEnabled`）

## 2. 玻璃材质（核心视觉）

### 2.1 实现方案（最终定论）
- 主方案 = WinUI3 官方 `SystemBackdrop`（DesktopAcrylicBackdrop）
- 兜底 = DWM 38（`DWMWA_SYSTEMBACKDROP_TYPE`=38 / `DWMSBT_TRANSIENTWINDOW`=3）
  - Win11 24H2 上 AccentPolicy 已失效，必须 DWM 38
- 顶栏/底栏 = 官方 `AcrylicBrush`（tint + DWM 窗口级模糊）
  - **桌面版 AcrylicBrush 无 `BackgroundSource` 属性**（UWP 才有）

### 2.2 tint 规则
| 场景 | 规则 |
|---|---|
| 磨砂模式 | tint = `Max(AcrylicOpacity, 0.55)`（**下限 0.55 防滑块拉 0 全透明**） |
| 液态玻璃 | tint 下限 **0.35** + `TintLuminosityOpacity 0.45` + 四周 1px 高光描边 |
| 浅色主题 | 70% 白面板（隐约轮廓档：卡片滑过见轮廓不见内容） |
| 深色主题 | 58% 白面板 |
| 侧边栏 | SplitView.Pane 宿主半透明 tint（60%）+ DWM 模糊 |

### 2.3 平台硬限制（实证定论，勿再投入）
1. **`DisplacementMapEffect` 被 CompositionEffectFactory 拒绝**（"Unsupported effect type"）→ 窗口内扭曲无法实现
2. **BackdropBrush 只采样窗口背后（DWM 层），不采样窗口内 XAML 内容**
   - 实证：滚动前后顶栏区像素差 1.7 vs 内容区 84.7
   - → 顶栏无法实时模糊滚动中的卡片；低频快照方案用户否决（抽动）
3. 液态玻璃最终形态 = **更透 tint + 高光描边**（最大近似，用户已接受）

## 3. 布局规范

- **悬浮层（顶栏/底栏）**：与内容同 Grid 格 + `VerticalAlignment`（Top/Bottom）+ **声明在 ScrollViewer 之后**（Z 序）
  - 禁止 `Panel.ZIndex`（同格层级用声明顺序）
- **ScrollViewer 留白**：用**内容 Grid 的 Margin**（顶部 132 = 悬浮顶栏高度；底部 0 = 卡片可滚入悬浮底栏透出）
  - 禁止 Padding 做留白（滚动内容会被裁剪）
- **状态栏必须悬浮在底部**（Grid 底对齐，顶部 1px 玻璃边缘光 `HighlightLineBrush`）
- **导航**：NavigationView，`OpenPaneLength=210 / CompactPaneLength=48`
- **绝不设置 `NavView.Background`**（会挡住亚克力）

## 4. 交互规范

- **卡片抬起**：Margin 动画（6px）+ BackEase 回弹 —— 禁 hover 背景高亮、禁 RenderTransform
- **相邻避让**：卡片抬起时相邻卡片同步微让位
- **多选模式**：卡片左上角 ☐ 复选；状态栏显示选中数；批量操作按钮（收藏/取消收藏/分类/取消分类/重命名/导出/查重/清空图库）
- **批量操作语义**：有选中 → 只作用于选中的；无选中 → 作用于当前筛选结果（对话框内明示）
- **删除**：永不触碰本地文件（全局零 `File.Delete`；「清空图库」= 移除图库条目）
- **导入**：文件夹（递归扫描）/ 文件（多选）/ 自动监控（FolderWatcher，可增删）
- **所有选择器**（FolderPicker/FileOpenPicker）：WinUI3 桌面非打包必须 `InitializeWithWindow.Initialize(picker, hwnd)`，否则静默失败

## 5. 响应式与主题

- 主题资源（ThemeResource）引用处即时更新；硬编码色按 `IsDark()` 分支
- 内容区半透明（alpha clamp 8–250）让窗口背景透出
- 背景三模式：主题色 / 纯色 / 自定义图片

## 6. 发布规范

- 非打包模式：`dotnet build -c Release` + 复制到发布目录（**禁 `dotnet publish`**，崩 `0xC000027B`）
- `PublishReadyToRun=false`、自包含 `PhotoAlbum.exe`、win-x64
- 版本号：csproj `<Version>` + `<FileVersion>` 同步
- 资产上传：>50MB 经代理 gh 上传会 EOF → **浏览器拖拽到 Release 页**
