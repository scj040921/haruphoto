# 更新与发布通道

## 应用端

设置 → 关于与更新 → 更新通道：Stable 或 Nightly。默认使用安装包所属通道；已有明确的通道偏好继续保留。
Stable 从 `GET /repos/scj040921/haruphoto/releases/latest` 获取正式版，Nightly 从 Releases 列表中选取 `nightly-YYYYMMDD-run` 预发行版。
只提示，不强制安装；跨通道安装可能回退版本。自动检查间隔十二小时，修改通道会重置检查时间。
Nightly 用日期和构建编号判断新旧，不依赖人为扩大软件版本号。同一 Nightly 不重复安装，同通道不自动回退基础版本。

第三方公益代理 `gh-proxy.org` 仅在直连公开 Release 资产失败时使用，设置中可关闭。
代理不是系统代理，不代理 GitHub API，不发送身份凭据、照片或图库信息。完整包 SHA256 必须匹配 GitHub HTTPS API 直接给出的 digest；无可信校验值则拒绝更新。
下载先完成、校验再退出，安装前核对包内版本/通道/标签及逐文件哈希，替换失败保留或恢复旧安装。
公共服务的可用性不保证；使用规则见 [服务方文档](https://gh-proxy.org/docs/github-accelerator)。

## GitHub Actions

| 事件 | 行为 |
| --- | --- |
| 推送源码到 master | 立即编译、自检，生成七天保留的 CI 安装包产物，不公开发布 |
| 推送 vX.Y.Z / VX.Y.Z 标签 | 编译标签对应的源码，完整测试通过才发布 Stable 并更新 latest |
| 每天 18:23 UTC（北京时间次日 02:23） | 对比远端 master commit 与上一 Nightly，变化时编译并发布新的 Nightly prerelease |
| 手动运行，channel=Nightly | 立即进行每日检查；force=true 可强制生成新测试版 |

这是同步远端仓库已提交的源码：本地保存文件不会自动 commit/push。仓库 owner 仍负责将自己的源码改动提交并推送到 master。
GitHub 调度可能延迟；公共仓库长期无活动时定时工作流可能暂停，届时需在 Actions 中恢复。没有为此安装本地后台同步软件。

Stable 和 Nightly 都包含完整自包含 x64 EXE 安装器、便携 ZIP、SHA256SUMS.txt、update.json（给旧稳定版兼容使用）。
包内 `build-info.json` 和 `package-manifest.json` 记录版本、通道、完整源码 SHA、不可变标签；Nightly 不使用 `latest`，不替换已有公开安装包。
相同 Stable 标签的重跑不会覆盖已发布包。源码有变化应生成新标签。

旧客户端首次获取双通道设置，需要先安装 v1.3.3 或新 Nightly；本次未自动替换正在运行的桌面程序。
