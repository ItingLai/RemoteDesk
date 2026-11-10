using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using Microsoft.Win32;

namespace RemoteDesk.Installer;

/// <summary>
/// RemoteDesk 安裝與解除安裝核心引擎
/// </summary>
public static class InstallerEngine
{
    public const string AppName = "RemoteDesk";
    public const string AppDisplayName = "RemoteDesk";
    public const string AppVersion = "1.0.1";
    public const string AppPublisher = "RemoteDesk Team";

    public static bool IsAdministrator()
    {
        try
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static string GetDefaultInstallDirectory()
    {
        if (IsAdministrator())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
        }
        else
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
        }
    }

    public static long GetAvailableFreeSpace(string path)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "C:\\";
            var drive = new DriveInfo(root);
            return drive.AvailableFreeSpace;
        }
        catch
        {
            return -1;
        }
    }

    public static void CloseRunningApp()
    {
        try
        {
            var processes = Process.GetProcessesByName(AppName);
            foreach (var p in processes)
            {
                try
                {
                    p.CloseMainWindow();
                    if (!p.WaitForExit(1500))
                    {
                        p.Kill();
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    public static void Install(
        string targetDirectory,
        bool createDesktopShortcut,
        bool createStartMenuShortcut,
        Action<int, string> progressCallback)
    {
        progressCallback(5, "正在檢查執行中之程序...");
        CloseRunningApp();

        progressCallback(15, "正在建立安裝資料夾...");
        Directory.CreateDirectory(targetDirectory);

        progressCallback(25, "正在解壓縮程式檔案...");
        var asm = Assembly.GetExecutingAssembly();
        using (var stream = asm.GetManifestResourceStream("RemoteDesk.Installer.Resources.payload.zip"))
        {
            if (stream == null)
            {
                throw new InvalidOperationException("安裝程式封裝資源損毀 (找不到 payload.zip)。");
            }

            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            int total = archive.Entries.Count;
            int count = 0;

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    // Directory entry
                    string dirPath = Path.Combine(targetDirectory, entry.FullName);
                    Directory.CreateDirectory(dirPath);
                    continue;
                }

                string destPath = Path.Combine(targetDirectory, entry.FullName);
                string? destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                entry.ExtractToFile(destPath, overwrite: true);
                count++;
                int pct = 25 + (int)((float)count / total * 45);
                progressCallback(pct, $"正在解壓縮：{entry.Name}");
            }
        }

        string exePath = Path.Combine(targetDirectory, $"{AppName}.exe");
        string uninstallerPath = Path.Combine(targetDirectory, "uninstall.exe");

        progressCallback(75, "正在建立解除安裝程式...");
        try
        {
            string currentExe = Environment.ProcessPath ?? "";
            if (!string.IsNullOrEmpty(currentExe) && File.Exists(currentExe))
            {
                File.Copy(currentExe, uninstallerPath, overwrite: true);
            }
        }
        catch { }

        string iconPath = Path.Combine(targetDirectory, "app.ico");
        string iconLocation = File.Exists(iconPath) ? $"{iconPath},0" : $"{exePath},0";

        if (createDesktopShortcut)
        {
            progressCallback(82, "正在建立桌面捷徑...");
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string lnkPath = Path.Combine(desktop, $"{AppDisplayName}.lnk");
                CreateShortcut(lnkPath, exePath, AppDisplayName, iconLocation, AppName);
            }
            catch { }
        }

        if (createStartMenuShortcut)
        {
            progressCallback(88, "正在建立「開始」功能表捷徑...");
            try
            {
                string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                string appMenuDir = Path.Combine(programs, AppDisplayName);
                Directory.CreateDirectory(appMenuDir);
                string lnkPath = Path.Combine(appMenuDir, $"{AppDisplayName}.lnk");
                CreateShortcut(lnkPath, exePath, AppDisplayName, iconLocation, AppName);

                string uninstallerLnk = Path.Combine(appMenuDir, "解除安裝 RemoteDesk.lnk");
                CreateShortcut(uninstallerLnk, uninstallerPath, "解除安裝 RemoteDesk", $"{uninstallerPath},0");
            }
            catch { }
        }

        progressCallback(95, "正在註冊 Windows 應用程式資訊...");
        RegisterUninstallInfo(targetDirectory, uninstallerPath, exePath);

        progressCallback(100, "安裝完成！");
    }

    public static void CreateShortcut(string shortcutPath, string targetPath, string description, string? iconLocation = null, string? appUserModelId = null)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null)
            {
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = targetPath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
                shortcut.Description = description;
                if (!string.IsNullOrEmpty(iconLocation))
                {
                    shortcut.IconLocation = iconLocation;
                }
                shortcut.Save();
            }

            if (!string.IsNullOrEmpty(appUserModelId))
            {
                try
                {
                    string safePath = shortcutPath.Replace("'", "''");
                    string safeAumid = appUserModelId.Replace("'", "''");
                    string psCmd = $"$link = New-Object -ComObject ShellLink; $link.Load('{safePath}', 0);"; // fallback handled by EnsureShortcutAndIdentity
                }
                catch { }
            }
        }
        catch { }
    }

    private static void RegisterUninstallInfo(string installDir, string uninstallerPath, string exePath)
    {
        try
        {
            string iconPath = Path.Combine(installDir, "app.ico");
            string iconLocation = File.Exists(iconPath) ? iconPath : $"{exePath},0";

            // 寫入 HKCU (無須系統管理員提權) 或 HKLM
            RegistryKey root = IsAdministrator() ? Registry.LocalMachine : Registry.CurrentUser;
            using var key = root.CreateSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppName}");
            if (key != null)
            {
                key.SetValue("DisplayName", AppDisplayName);
                key.SetValue("DisplayIcon", iconLocation);
                key.SetValue("DisplayVersion", AppVersion);
                key.SetValue("Publisher", AppPublisher);
                key.SetValue("InstallLocation", installDir);
                key.SetValue("UninstallString", $"\"{uninstallerPath}\" /uninstall");
                key.SetValue("QuietUninstallString", $"\"{uninstallerPath}\" /uninstall /quiet");
                key.SetValue("EstimatedSize", 15000, RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }

            using var aumidKey = root.CreateSubKey($@"Software\Classes\AppUserModelId\{AppName}");
            if (aumidKey != null)
            {
                aumidKey.SetValue("DisplayName", AppDisplayName);
                aumidKey.SetValue("IconUri", iconLocation);
                aumidKey.SetValue("ShowInSettings", 1, RegistryValueKind.DWord);
                aumidKey.SetValue("IconBackgroundColor", "0");
            }
        }
        catch { }
    }

    public static void RemoveUninstallInfo()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppName}", throwOnMissingSubKey: false);
        }
        catch { }

        try
        {
            if (IsAdministrator())
            {
                Registry.LocalMachine.DeleteSubKeyTree($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppName}", throwOnMissingSubKey: false);
            }
        }
        catch { }
    }

    public static void Uninstall(string installDir, bool quiet = false)
    {
        CloseRunningApp();

        // 1. 刪除桌面捷徑
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string lnkPath = Path.Combine(desktop, $"{AppDisplayName}.lnk");
            if (File.Exists(lnkPath)) File.Delete(lnkPath);
        }
        catch { }

        // 2. 刪除開始功能表
        try
        {
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string appMenuDir = Path.Combine(programs, AppDisplayName);
            if (Directory.Exists(appMenuDir)) Directory.Delete(appMenuDir, recursive: true);
        }
        catch { }

        // 3. 刪除登錄檔
        RemoveUninstallInfo();

        // 4. 自我延遲刪除檔案目錄 (透過 cmd start 延遲刪除)
        try
        {
            if (Directory.Exists(installDir))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c timeout /t 2 /nobreak > nul & rmdir /s /q \"{installDir}\"",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
        }
        catch { }
    }
}
