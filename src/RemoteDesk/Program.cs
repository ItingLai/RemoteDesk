using RemoteDesk.Common;
using RemoteDesk.Host;
using RemoteDesk.Net;
using RemoteDesk.Viewer;

namespace RemoteDesk;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // 檢查命令列參數
        if (args.Length > 0)
        {
            if (args.Contains("--help") || args.Contains("-h"))
            {
                MessageBox.Show(
                    "RemoteDesk 內網原生遠端桌面\n\n" +
                    "用法：\n" +
                    "  RemoteDesk.exe                    啟動圖形介面主程式\n" +
                    "  RemoteDesk.exe --connect <ID/IP>   直接連線至指定遠端主機\n" +
                    "  RemoteDesk.exe --headless          無介面背景服務模式\n",
                    "RemoteDesk 說明",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            int connectIdx = Array.IndexOf(args, "--connect");
            if (connectIdx >= 0 && connectIdx + 1 < args.Length)
            {
                string target = args[connectIdx + 1];
                var viewerCfg = ViewerConfig.Load();
                var hostCfg = HostConfig.Load();
                var discovery = new LanDiscovery(hostCfg.DeviceId);
                discovery.Start();

                var session = new ViewerSession(target, viewerCfg, discovery);
                try
                {
                    string? pwd = viewerCfg.GetSavedPassword(target);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    session.ConnectAsync(pwd, cts.Token).GetAwaiter().GetResult();
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

        // 預設啟動完整圖形主介面 (包含被控服務與控制端功能)
        Application.Run(new MainForm());
    }
}
