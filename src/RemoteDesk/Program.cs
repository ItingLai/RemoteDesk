using RemoteDesk.Common;
using RemoteDesk.Host;
using RemoteDesk.Native;
using RemoteDesk.Net;
using RemoteDesk.Viewer;

namespace RemoteDesk;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 設定應用程式專屬 AppUserModelID 與捷徑圖標關聯，以利 Windows 10/11 系統通知 Attribution Area 精確呈現品牌 Logo
        try
        {
            AppBranding.EnsureShortcutAndIdentity();
        }
        catch { }

        ApplicationConfiguration.Initialize();

        string? profile = null;
        int? customPort = null;

        // 檢查命令列參數
        if (args.Length > 0)
        {
            if (args.Contains("--help") || args.Contains("-h"))
            {
                MessageBox.Show(
                    "RemoteDesk 內網原生遠端桌面\n\n" +
                    "用法：\n" +
                    "  RemoteDesk.exe                                  啟動圖形介面主程式\n" +
                    "  RemoteDesk.exe --connect <ID/IP>                 直接連線至指定遠端主機\n" +
                    "  RemoteDesk.exe --profile <名稱> --port <連接埠>   多開獨立實例 (單機測試必備)\n" +
                    "  RemoteDesk.exe --headless                        無介面背景服務模式\n",
                    "RemoteDesk 說明",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            int profileIdx = Array.IndexOf(args, "--profile");
            if (profileIdx >= 0 && profileIdx + 1 < args.Length)
            {
                profile = args[profileIdx + 1];
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteDesk", "Profiles", profile);
                AppConfig.OverrideDataDirectory(dir);
            }

            int portIdx = Array.IndexOf(args, "--port");
            if (portIdx >= 0 && portIdx + 1 < args.Length && int.TryParse(args[portIdx + 1], out int parsedPort))
            {
                customPort = parsedPort;
            }

            int connectIdx = Array.IndexOf(args, "--connect");
            if (connectIdx >= 0 && connectIdx + 1 < args.Length)
            {
                string target = args[connectIdx + 1];
                var viewerCfg = ViewerConfig.Load();
                var hostCfgForViewer = HostConfig.Load();
                var discovery = new LanDiscovery(hostCfgForViewer.DeviceId);
                discovery.Start();

                var session = new ViewerSession(target, viewerCfg, discovery);
                try
                {
                    string? pwd = viewerCfg.GetSavedPassword(target);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    session.ConnectAsync(pwd, cts.Token).GetAwaiter().GetResult();
                    if (session.ClearedStaleSavedPassword)
                    {
                        viewerCfg.Save();
                    }
                    Application.Run(new ViewerForm(session));
                    return;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"無法連線至 {target}：{ex.Message}", "連線失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }
        }

        // 先載入既有 HostConfig 以取得穩定的 DeviceId，再進行單一主程式實例鎖定（避免雙開互搶同一 DeviceId 與 Signaling 工作階段）
        var initialHostCfg = HostConfig.Load();
        using var singleInstance = SingleInstanceGuard.TryAcquire(profile, initialHostCfg.DeviceId);
        if (singleInstance == null)
        {
            Log.Info($"[Startup] 偵測到相同 Profile/DeviceId 主程式實例已在執行，略過重複啟動 (Profile={profile ?? "default"}, DeviceId={initialHostCfg.DeviceId})。");
            bool activated = SingleInstanceGuard.NotifyExistingInstance(profile, initialHostCfg.DeviceId);
            if (!activated)
            {
                MessageBox.Show(
                    $"RemoteDesk 已在執行中（裝置 ID：{AppConfig.FormatId(initialHostCfg.DeviceId)}）。\n為避免重複註冊與工作階段競爭，同一裝置 ID 僅允許一個主程式實例。",
                    "RemoteDesk 已在執行",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return;
        }

        string appVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "2.0.2";
        Log.Info($"[Startup] 已取得單一主程式實例鎖 (Version={appVersion}, Profile={profile ?? "default"}, DeviceId={initialHostCfg.DeviceId}, Mutex={singleInstance.MutexName})。");

        if (customPort.HasValue)
        {
            initialHostCfg.TcpPort = customPort.Value;
            if (!string.IsNullOrEmpty(profile))
            {
                initialHostCfg.DeviceName = $"{Environment.MachineName} [{profile}]";
            }
            initialHostCfg.TrySave();
        }

        // 預設啟動完整圖形主介面 (包含被控服務與控制端功能)
        using var mainForm = new MainForm();
        RegisteredWaitHandle? waitHandle = ThreadPool.RegisterWaitForSingleObject(
            singleInstance.ActivationEvent,
            static (state, timedOut) =>
            {
                if (timedOut || state is not Form form || form.IsDisposed) return;
                try
                {
                    if (!form.IsHandleCreated) return;
                    form.BeginInvoke(() =>
                    {
                        if (form.IsDisposed) return;
                        if (form.WindowState == FormWindowState.Minimized)
                        {
                            form.WindowState = FormWindowState.Normal;
                        }
                        form.Show();
                        form.Activate();
                        form.BringToFront();
                        Win32.SetForegroundWindow(form.Handle);
                    });
                }
                catch { }
            },
            mainForm,
            Timeout.Infinite,
            executeOnlyOnce: false);

        try
        {
            Application.Run(mainForm);
        }
        finally
        {
            waitHandle?.Unregister(null);
        }
    }
}
