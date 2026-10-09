using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace HaruphotoInstaller;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--self-test", var report])
        {
            try { File.WriteAllText(report, Installer.SelfTest()); return 0; }
            catch (Exception ex) { File.WriteAllText(report, ex.ToString()); return 1; }
        }
        using var instance = new Mutex(true, "haruphoto_setup_instance", out var first);
        if (!first) { MessageBox.Show("安装器已在运行，请使用已打开的安装窗口。", "haruphoto 安装"); return 1; }
        ApplicationConfiguration.Initialize();
        try { Installer.CheckPlatform(); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "haruphoto 安装", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        Application.Run(new SetupWindow());
        return 0;
    }
}

internal sealed class SetupWindow : Form
{
    private readonly Button install = new() { Text = "安装", AutoSize = true };
    private readonly Button close = new() { Text = "关闭", AutoSize = true };
    private readonly CheckBox launch = new() { Text = "安装完成后启动相册", Checked = true, AutoSize = true };
    private readonly Label status = new() { Text = "准备安装", AutoSize = false, Height = 58, Dock = DockStyle.Top };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Top, Height = 12 };
    private bool busy;

    public SetupWindow()
    {
        Text = "haruphoto " + Installer.Version + " 安装";
        ClientSize = new Size(530, 320);
        Font = new Font("Microsoft YaHei UI", 10);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 6 };
        layout.Controls.Add(new Label { Text = "安装 haruphoto 照片管理", AutoSize = true, Font = new Font(Font.FontFamily, 17, FontStyle.Bold) });
        layout.Controls.Add(new Label { Text = "离线安装 · 自带运行时 · 无需管理员权限\n升级不会删除照片、图库和设置。", AutoSize = true, Margin = new Padding(0, 12, 0, 10) });
        layout.Controls.Add(new TextBox { Text = Installer.DefaultRoot, ReadOnly = true, Dock = DockStyle.Top });
        layout.Controls.Add(status);
        layout.Controls.Add(progress);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.Add(install); buttons.Controls.Add(close); buttons.Controls.Add(launch);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        close.Click += (_, _) => Close();
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
        install.Click += async (_, _) =>
        {
            if (Installer.InstalledProcesses().Any())
            {
                MessageBox.Show("请先关闭已安装的 haruphoto，再点击安装。其他目录中的开发版不会被关闭。", Text);
                return;
            }
            busy = true; install.Enabled = close.Enabled = launch.Enabled = false;
            progress.Style = ProgressBarStyle.Marquee;
            try
            {
                var notices = new Progress<string>(message => status.Text = message);
                var warning = await Task.Run(() => Installer.Install(Installer.DefaultRoot, true, notices));
                progress.Style = ProgressBarStyle.Continuous; progress.Value = 100;
                status.Text = "安装完成" + (warning.Length == 0 ? "，可以从桌面或开始菜单启动。" : "。" + warning);
                install.Visible = false;
                if (launch.Checked)
                {
                    var proc = Process.Start(new ProcessStartInfo(Path.Combine(Installer.DefaultRoot, "PhotoAlbum.exe")) { UseShellExecute = true, WorkingDirectory = Installer.DefaultRoot });
                    await Task.Delay(3000);
                    if (proc is not null && proc.HasExited && proc.ExitCode != 0)
                    {
                        status.Text = "安装已完成，但自动启动失败。";
                        var failure = new InvalidOperationException($"程序启动失败（{proc.ExitCode:X8}）。请把此提示和 Windows 版本发给开发者。");
                        MessageBox.Show(failure.Message + "\n日志：" + Installer.WriteFailure(failure), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                progress.Style = ProgressBarStyle.Continuous; progress.Value = 0;
                status.Text = "安装未完成。";
                var log = Installer.WriteFailure(ex);
                MessageBox.Show(ex.Message + "\n\n诊断日志：" + log, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                install.Enabled = true;
            }
            finally { busy = false; close.Enabled = launch.Enabled = true; }
        };
    }
}

internal static class Installer
{
    internal static string Version => Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);
    internal static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "haruphoto");
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\haruphoto";

    internal static void CheckPlatform()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new InvalidOperationException("需要 Windows 10 1809（17763）或更高版本，或 Windows 11。");
        if (!Environment.Is64BitOperatingSystem)
            throw new InvalidOperationException("此安装包需要 64 位 Windows，不支持 32 位 Windows。");
    }

    internal static IEnumerable<Process> InstalledProcesses() => Process.GetProcessesByName("PhotoAlbum").Where(p =>
    {
        try { return string.Equals(p.MainModule?.FileName, Path.Combine(DefaultRoot, "PhotoAlbum.exe"), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    });

    internal static string Install(string root, bool integrate, IProgress<string>? progress = null, bool failBeforeCommit = false)
    {
        root = Path.GetFullPath(root);
        if (integrate && !string.Equals(root, DefaultRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid installation target.");
        RejectReparsePoint(root);
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        string stage = root + ".stage-" + Guid.NewGuid().ToString("N");
        string backup = root + ".previous-" + Guid.NewGuid().ToString("N");
        bool movedOld = false, committed = false;
        try
        {
            progress?.Report("正在解压并校验完整安装包…");
            Directory.CreateDirectory(stage);
            ExtractPayload(stage);
            ValidatePayload(stage);
            // Validation completes before an existing installation is touched.
            if (integrate && InstalledProcesses().Any()) throw new IOException("请先关闭已安装的 haruphoto，再重新安装。");
            if (Directory.Exists(root)) { Directory.Move(root, backup); movedOld = true; }
            if (failBeforeCommit) throw new IOException("Simulated interrupted installation.");
            Directory.Move(stage, root); committed = true;
        }
        catch
        {
            if (movedOld && !committed) Directory.Move(backup, root);
            throw;
        }
        finally { DeleteOwnedFolder(stage, root + ".stage-"); }
        string warning = "";
        if (integrate)
        {
            progress?.Report("正在创建快捷方式和卸载入口…");
            try { Integrate(root); }
            catch (Exception ex) { warning = "快捷方式或卸载入口创建失败，可直接运行安装目录内 PhotoAlbum.exe。"; WriteFailure(ex); }
        }
        if (movedOld)
        {
            try { DeleteOwnedFolder(backup, root + ".previous-"); }
            catch (Exception ex) { warning += "旧版本备份已保留。"; WriteFailure(ex); }
        }
        return warning;
    }

    private static void ExtractPayload(string stage)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("haruphoto.payload.zip") ?? throw new IOException("安装器缺少文件包。");
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(stage, entry.FullName));
            if (!target.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("安装包包含不安全的路径。");
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target);
        }
    }

    private static void ValidatePayload(string stage)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(stage, "package-manifest.json")));
        var manifest = document.RootElement;
        if (manifest.GetProperty("version").GetString() != Version) throw new IOException("安装器与程序版本不匹配。");
        foreach (var file in manifest.GetProperty("files").EnumerateArray())
        {
            var path = Path.GetFullPath(Path.Combine(stage, file.GetProperty("path").GetString()!));
            if (!path.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid manifest path.");
            using var contents = File.OpenRead(path);
            var digest = Convert.ToHexString(SHA256.HashData(contents));
            if (!string.Equals(digest, file.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)) throw new IOException("安装包损坏：" + Path.GetFileName(path));
        }
        foreach (var required in new[] { "PhotoAlbum.exe", "PhotoAlbum.dll", "PhotoAlbum.runtimeconfig.json", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "System.Private.CoreLib.dll", "Microsoft.UI.Xaml.dll", "PhotoAlbum.pri", "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "tools/uninstall.ps1", "tools/update.ps1" })
            if (!File.Exists(Path.Combine(stage, required))) throw new IOException("安装包缺少运行文件：" + required);
        using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(stage, "PhotoAlbum.runtimeconfig.json")));
        var options = runtime.RootElement.GetProperty("runtimeOptions");
        if (options.TryGetProperty("framework", out _) || options.TryGetProperty("frameworks", out _)) throw new IOException("程序包未包含完整 .NET 运行时。");
    }

    private static void Integrate(string root)
    {
        var exe = Path.Combine(root, "PhotoAlbum.exe");
        foreach (var directory in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
        {
            Directory.CreateDirectory(directory);
            var type = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic shortcut = shell.CreateShortcut(Path.Combine(directory, "haruphoto.lnk"));
            try { shortcut.TargetPath = exe; shortcut.WorkingDirectory = root; shortcut.IconLocation = exe + ",0"; shortcut.Save(); }
            finally { Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
        }
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "haruphoto"); key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "scj040921"); key.SetValue("InstallLocation", root);
        key.SetValue("DisplayIcon", exe); key.SetValue("NoModify", 1); key.SetValue("NoRepair", 1);
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        key.SetValue("UninstallString", $"\"{powershell}\" -NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(root, "tools", "uninstall.ps1")}\"");
    }

    private static void RejectReparsePoint(string path)
    {
        for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("安装目录不能是符号链接或目录联接。");
    }

    private static void DeleteOwnedFolder(string path, string prefix)
    {
        if (!Directory.Exists(path)) return;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(prefix), StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(full[prefix.Length..], "N", out _)) throw new IOException("Unsafe cleanup target.");
        RejectReparsePoint(full);
        Directory.Delete(full, true);
    }

    internal static string WriteFailure(Exception ex)
    {
        var path = Path.Combine(Path.GetTempPath(), "haruphoto-install-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
        try { File.WriteAllText(path, $"haruphoto {Version}\n{RuntimeInformation.OSDescription}\n{RuntimeInformation.OSArchitecture}\n{ex}"); }
        catch { return "无法写入日志：" + path; }
        return path;
    }

    internal static string SelfTest()
    {
        CheckPlatform();
        var root = Path.Combine(Path.GetTempPath(), "haruphoto-installer-test-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "installed");
        Directory.CreateDirectory(root);
        try
        {
            Install(target, false);
            ValidatePayload(target);
            File.WriteAllText(Path.Combine(target, "old-version-marker"), "preserve me");
            try { Install(target, false, failBeforeCommit: true); throw new Exception("Rollback test did not fail."); }
            catch (IOException ex) when (ex.Message == "Simulated interrupted installation.") { }
            if (!File.Exists(Path.Combine(target, "old-version-marker"))) throw new Exception("Rollback lost the existing version.");
            ValidatePayload(target);
            Install(target, false);
            if (File.Exists(Path.Combine(target, "old-version-marker"))) throw new Exception("Upgrade retained stale files.");
            ValidatePayload(target);
            if (Directory.GetDirectories(root).Length != 1) throw new Exception("Staging/backup folders were leaked.");
            return "PASS: platform; embedded payload hashes; all runtime dependencies; fresh offline install; interrupted upgrade rollback; clean upgrade; owned temporary cleanup.\n";
        }
        finally
        {
            if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())) throw new IOException("Unsafe test directory.");
            RejectReparsePoint(root); Directory.Delete(root, true);
        }
    }
}
