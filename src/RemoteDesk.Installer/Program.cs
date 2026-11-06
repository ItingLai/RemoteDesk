namespace RemoteDesk.Installer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // 檢查是否為解除安裝模式
        bool isUninstall = args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase) ||
                                         a.Equals("-uninstall", StringComparison.OrdinalIgnoreCase));
        bool isQuiet = args.Any(a => a.Equals("/quiet", StringComparison.OrdinalIgnoreCase) ||
                                     a.Equals("-quiet", StringComparison.OrdinalIgnoreCase));

        if (isUninstall)
        {
            string installDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');

            if (!isQuiet)
            {
                var result = MessageBox.Show(
                    "確定要將 RemoteDesk 遠端桌面 從此電腦中完全解除安裝嗎？",
                    "RemoteDesk 解除安裝確認",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result != DialogResult.Yes)
                {
                    return;
                }
            }

            try
            {
                InstallerEngine.Uninstall(installDir, isQuiet);
                if (!isQuiet)
                {
                    MessageBox.Show(
                        "RemoteDesk 已成功從您的電腦中移除。",
                        "解除安裝完成",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                if (!isQuiet)
                {
                    MessageBox.Show(
                        $"解除安裝過程中發生錯誤：\n{ex.Message}",
                        "解除安裝失敗",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
            return;
        }

        // 正常安裝精靈模式
        Application.Run(new InstallerForm());
    }
}
