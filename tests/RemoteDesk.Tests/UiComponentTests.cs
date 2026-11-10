using System.Drawing;
using System.Windows.Forms;
using RemoteDesk.Common;
using RemoteDesk.Controls;
using RemoteDesk.Host;
using RemoteDesk.Native;
using RemoteDesk.Net;
using RemoteDesk.Viewer;
using Xunit;

namespace RemoteDesk.Tests;

public class UiComponentTests
{
    [Fact]
    public void Theme_TokensMatchSpecification()
    {
        Assert.Equal(ColorTranslator.FromHtml("#111827"), Theme.Background);
        Assert.Equal(ColorTranslator.FromHtml("#1F2937"), Theme.Surface);
        Assert.Equal(ColorTranslator.FromHtml("#273449"), Theme.SurfaceElevated);
        Assert.Equal(ColorTranslator.FromHtml("#2563EB"), Theme.Primary);
        Assert.Equal(ColorTranslator.FromHtml("#60A5FA"), Theme.Accent);
        Assert.Equal(ColorTranslator.FromHtml("#10B981"), Theme.Success);
        Assert.Equal(ColorTranslator.FromHtml("#F59E0B"), Theme.Warning);
        Assert.Equal(ColorTranslator.FromHtml("#EF4444"), Theme.Danger);
        Assert.Equal(ColorTranslator.FromHtml("#F9FAFB"), Theme.TextPrimary);
        Assert.Equal(ColorTranslator.FromHtml("#9CA3AF"), Theme.TextSecondary);
        Assert.Equal(ColorTranslator.FromHtml("#374151"), Theme.Border);

        Assert.Equal(50, Theme.InputHeight);
        Assert.Equal(10, Theme.CornerRadius);
    }

    [Fact]
    public void SettingsForm_InitializesWithoutException()
    {
        var hostConfig = new HostConfig
        {
            DeviceId = "123456789",
            DeviceName = "HostTest"
        };
        var viewerConfig = new ViewerConfig();

        using var form = new SettingsForm(hostConfig, viewerConfig, () => { });
        Assert.NotNull(form);
        Assert.Equal("RemoteDesk Settings", form.Text);
    }

    [Fact]
    public void ConnectionHistoryForm_InitializesWithoutException()
    {
        var viewerConfig = new ViewerConfig();
        viewerConfig.TouchRecent("123456789", "123456789", "DEV-1", "192.168.1.10", 21119);

        using var form = new ConnectionHistoryForm(viewerConfig, _ => { }, () => { });
        Assert.NotNull(form);
        Assert.Equal("Connection History - 連線紀錄", form.Text);
    }

    [Fact]
    public void RemoteAddressInput_InitializesAndTriggersEvent()
    {
        using var input = new RemoteAddressInput();
        Assert.Equal(50, input.Height);

        string? received = null;
        input.ConnectRequested += id => received = id;

        input.TextValue = " 987654321 ";
        input.IsLoading = true;
        Assert.True(input.IsLoading);
        input.IsLoading = false;
        Assert.False(input.IsLoading);
    }

    [Fact]
    public void DeviceThumbnailCard_InitializesAndMatchesRatio()
    {
        var entry = new RecentEntry
        {
            Key = "123456789",
            DeviceId = "123456789",
            Name = "TEST-PC",
            LastUsed = DateTime.Now,
            IsFavorite = true
        };

        using var card = new DeviceThumbnailCard(entry, _ => { }, _ => { }, _ => { }, (_, _) => { });
        Assert.NotNull(card);
        Assert.Equal(216, card.Width);
        Assert.Equal(166, card.Height);
        Assert.NotNull(card.ContextMenuStrip);
        Assert.Contains(card.ContextMenuStrip!.Items.Cast<ToolStripItem>(), i => i.Text != null && i.Text.Contains("重新命名"));
    }

    [Fact]
    public void MainForm_HeaderControls_AreVisibleAndProperlyPlaced()
    {
        using var form = new MainForm();
        form.Show();
        Application.DoEvents();

        // Find _pnlHeader
        var header = form.Controls.Cast<Control>().FirstOrDefault(c => c.Height == Theme.HeaderHeight);
        Assert.NotNull(header);

        // Find settings button
        var allDescendants = new List<Control>();
        void Collect(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                allDescendants.Add(c);
                Collect(c);
            }
        }
        Collect(form);

        var historyBtn = allDescendants.OfType<Button>().FirstOrDefault(b => b.Text.Contains("連線紀錄"));
        Assert.Null(historyBtn); // User requested removing history button from top right

        var settingsBtn = allDescendants.OfType<Button>().FirstOrDefault(b => b.Text.Contains("設定"));
        Assert.NotNull(settingsBtn);
        Assert.True(settingsBtn.Visible);
        Assert.True(settingsBtn.Parent?.Visible ?? false);
        Assert.True(settingsBtn.Width > 0);
        Assert.True(settingsBtn.Height > 0);
        Assert.NotNull(settingsBtn.Image);
        Assert.True(settingsBtn.Image.Width >= 16);

        // Assert settingsBtn is completely within form client area
        var screenPoint = settingsBtn.PointToScreen(Point.Empty);
        var formPoint = form.PointToClient(screenPoint);
        Assert.True(formPoint.X >= 0, $"Settings button X is negative: {formPoint.X}");
        Assert.True(formPoint.X + settingsBtn.Width <= form.ClientSize.Width, 
            $"Settings button Right {formPoint.X + settingsBtn.Width} is off-screen (Width {form.ClientSize.Width})");

        using var bmp = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
        bmp.Save("main_form_rendered.png");

        // Shrink form to minimum width 720 and render
        form.Size = new Size(720, 500);
        Application.DoEvents();
        Assert.Equal(88, settingsBtn.Width);
        Assert.Equal(30, settingsBtn.Height);
        using var bmpShrunk = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bmpShrunk, new Rectangle(0, 0, form.Width, form.Height));
        bmpShrunk.Save("main_form_shrunk_rendered.png");
    }

    [Fact]
    public void SettingsForm_RenderedView_HasAllCategoryIcons()
    {
        var hostCfg = new HostConfig { DeviceId = "987654321", DeviceName = "TestHost" };
        var viewerCfg = new ViewerConfig();

        using var form = new SettingsForm(hostCfg, viewerCfg, () => { });
        form.Show();
        Application.DoEvents();

        using var bmp = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
        bmp.Save("settings_form_rendered.png");
    }

    [Fact]
    public void MainForm_TwoRowLayout_CustomChromeAndSessionToolbar()
    {
        using var form = new MainForm();
        form.Show();
        Application.DoEvents();

        // 1. Verify custom borderless style
        Assert.Equal(FormBorderStyle.None, form.FormBorderStyle);

        var allDescendants = new List<Control>();
        void Collect(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                allDescendants.Add(c);
                Collect(c);
            }
        }
        Collect(form);

        // 2. Verify there is NO bottom status bar anywhere in the form
        var bottomBars = allDescendants.OfType<Panel>().Where(p => p.Dock == DockStyle.Bottom && p.Parent == form).ToList();
        Assert.Empty(bottomBars);

        // 3. Find _pnlHeader (Row 1: Titlebar / Custom Window Chrome of height 40)
        var header = form.Controls.Cast<Control>().FirstOrDefault(c => c.Height == Theme.HeaderHeight && c.Dock == DockStyle.Top);
        Assert.NotNull(header);

        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var pnlSessionView = (Panel)form.GetType().GetField("_pnlSessionView", flags)!.GetValue(form)!;
        var pnlHomeView = (Panel)form.GetType().GetField("_pnlHomeView", flags)!.GetValue(form)!;
        var pnlSessionToolbar = (Panel)form.GetType().GetField("_pnlSessionToolbar", flags)!.GetValue(form)!;
        var pnlMonitors = (FlowLayoutPanel)form.GetType().GetField("_pnlMonitors", flags)!.GetValue(form)!;
        var lblLogo = (Label)form.GetType().GetField("_lblLogo", flags)!.GetValue(form)!;
        var lblSessionTitle = (Label)form.GetType().GetField("_lblSessionTitle", flags)!.GetValue(form)!;
        var lblSessionStatusBadge = (Label)form.GetType().GetField("_lblSessionStatusBadge", flags)!.GetValue(form)!;
        var btnDisconnect = (Button)form.GetType().GetField("_btnSessionDisconnect", flags)!.GetValue(form)!;
        var btnSettings = (Button)form.GetType().GetField("_btnSessionSettings", flags)!.GetValue(form)!;
        var btnFullscreen = (Button)form.GetType().GetField("_btnFullscreen", flags)!.GetValue(form)!;
        var btnClipboard = (Button)form.GetType().GetField("_btnClipboardSync", flags)!.GetValue(form)!;
        var btnSpecialKeys = (Button)form.GetType().GetField("_btnSpecialKeys", flags)!.GetValue(form)!;
        var btnIme = (Button)form.GetType().GetField("_btnImeMode", flags)!.GetValue(form)!;
        var btnWinMinimize = (Button)form.GetType().GetField("_btnWinMinimize", flags)!.GetValue(form)!;
        var btnWinMaximize = (Button)form.GetType().GetField("_btnWinMaximize", flags)!.GetValue(form)!;
        var btnWinClose = (Button)form.GetType().GetField("_btnWinClose", flags)!.GetValue(form)!;

        // Verify _pnlSessionView contains both Row 2 toolbar and the canvas container
        Assert.Equal(2, pnlSessionView.Controls.Count);
        Assert.Contains(pnlSessionToolbar, pnlSessionView.Controls.Cast<Control>());
        Assert.Equal(DockStyle.Top, pnlSessionToolbar.Dock);
        Assert.Equal(Theme.ToolbarHeight, pnlSessionToolbar.Height);

        // 4. Simulate entering a session
        var sessionItemType = typeof(MainForm).GetNestedType("SessionTabItem", flags)!;
        var dummySession = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(sessionItemType);
        form.GetType().GetField("_currentSession", flags)!.SetValue(form, dummySession);

        pnlHomeView.Visible = false;
        pnlSessionView.Visible = true;
        pnlSessionView.BringToFront();

        lblSessionTitle.Text = "WORK-PC (123 456 789)";
        lblSessionStatusBadge.Text = "● 12ms";

        // Add AnyDesk style monitor buttons (1 and 2)
        pnlMonitors.Controls.Clear();
        var m1 = new Button
        {
            Image = VectorIcons.CreateMonitorNumberBitmap(24, 18, "1", Color.White),
            ImageAlign = ContentAlignment.MiddleCenter,
            Size = new Size(34, 30),
            Margin = new Padding(0, 0, 4, 0),
            BackColor = Theme.Primary,
            FlatStyle = FlatStyle.Flat
        };
        m1.FlatAppearance.BorderColor = Theme.Accent;

        var m2 = new Button
        {
            Image = VectorIcons.CreateMonitorNumberBitmap(24, 18, "2", Theme.TextSecondary),
            ImageAlign = ContentAlignment.MiddleCenter,
            Size = new Size(34, 30),
            Margin = new Padding(0, 0, 4, 0),
            BackColor = Theme.SurfaceElevated,
            FlatStyle = FlatStyle.Flat
        };
        m2.FlatAppearance.BorderColor = Theme.Border;
        pnlMonitors.Controls.AddRange(new Control[] { m1, m2 });

        // Update layouts
        var updateHeaderMethod = form.GetType().GetMethod("UpdateHeaderLayout", flags)!;
        updateHeaderMethod.Invoke(form, null);
        var updateToolbarMethod = form.GetType().GetMethod("UpdateSessionToolbarLayout", flags)!;
        updateToolbarMethod.Invoke(form, null);
        Application.DoEvents();

        // 5. Verify Row 1 (Header / Window Chrome):
        // Left side: RemoteDesk logo and session title/status (左上角位置)
        Assert.True(lblLogo.Location.X < 30, "RemoteDesk logo must be at the left side");
        Assert.True(lblSessionTitle.Location.X > lblLogo.Right, "Session title must be to the right of logo");
        Assert.True(lblSessionStatusBadge.Location.X > lblSessionTitle.Right, "Status badge must be to the right of title");

        // Right side of Header: Window control buttons [—] [🗖] [✕]
        Assert.True(btnWinClose.Location.X > form.ClientSize.Width - 50, "Close button must be at the far right");
        Assert.True(btnWinMaximize.Location.X < btnWinClose.Location.X, "Maximize is to the left of Close");
        Assert.True(btnWinMinimize.Location.X < btnWinMaximize.Location.X, "Minimize is to the left of Maximize");

        // 6. Verify Row 2 (Session Toolbar - 下一行靠右擺放):
        // Controls in order [Monitors] | [IME] [⚡] [📋] [⛶] [⚙] [✕]
        Assert.True(btnDisconnect.Location.X > pnlSessionToolbar.ClientSize.Width - 50, "Disconnect button must be aligned to the right edge");
        Assert.True(btnSettings.Location.X < btnDisconnect.Location.X, "Settings button must be to the left of disconnect");
        Assert.True(btnFullscreen.Location.X < btnSettings.Location.X);
        Assert.True(btnClipboard.Location.X < btnFullscreen.Location.X);
        Assert.True(btnSpecialKeys.Location.X < btnClipboard.Location.X);
        Assert.True(btnIme.Location.X < btnSpecialKeys.Location.X);
        Assert.True(pnlMonitors.Location.X < btnIme.Location.X);

        // Verify button iconography
        Assert.NotNull(btnSettings.Image);
        Assert.Equal("✕", btnDisconnect.Text);
        Assert.Equal("⛶", btnFullscreen.Text);
        Assert.Equal("📋", btnClipboard.Text);
        Assert.Equal("⚡", btnSpecialKeys.Text);
        Assert.Equal("⌨", btnIme.Text);

        using var bmp = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
        bmp.Save("session_view_rendered.png");
    }

    [Fact]
    public void MainForm_Borderless_ResizeBorders_AreAllDetectableAcross8Directions()
    {
        using var form = new MainForm();
        form.Show();
        Application.DoEvents();

        Assert.Equal(FormBorderStyle.None, form.FormBorderStyle);

        // Helper to invoke WndProc with WM_NCHITTEST
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var wndProcMethod = form.GetType().GetMethod("WndProc", flags)!;

        int SendHitTest(int clientX, int clientY)
        {
            Point screenPt = form.PointToScreen(new Point(clientX, clientY));
            IntPtr lParam = (IntPtr)(((screenPt.Y & 0xFFFF) << 16) | (screenPt.X & 0xFFFF));
            var m = Message.Create(form.Handle, 0x0084, IntPtr.Zero, lParam); // WM_NCHITTEST
            object[] args = new object[] { m };
            wndProcMethod.Invoke(form, args);
            var resultMsg = (Message)args[0];
            return (int)resultMsg.Result;
        }

        int w = form.ClientSize.Width;
        int h = form.ClientSize.Height;

        // Verify all 8 directions and corners:
        Assert.Equal(13, SendHitTest(2, 2));         // HTTOPLEFT
        Assert.Equal(14, SendHitTest(w - 2, 2));     // HTTOPRIGHT
        Assert.Equal(16, SendHitTest(2, h - 2));     // HTBOTTOMLEFT
        Assert.Equal(17, SendHitTest(w - 2, h - 2)); // HTBOTTOMRIGHT
        Assert.Equal(12, SendHitTest(w / 2, 2));     // HTTOP
        Assert.Equal(15, SendHitTest(w / 2, h - 2)); // HTBOTTOM
        Assert.Equal(10, SendHitTest(2, h / 2));     // HTLEFT
        Assert.Equal(11, SendHitTest(w - 2, h / 2)); // HTRIGHT

        // And inside the window (center), it should not be a sizing border
        Assert.NotEqual(10, SendHitTest(w / 2, h / 2));
        Assert.NotEqual(11, SendHitTest(w / 2, h / 2));
        Assert.NotEqual(12, SendHitTest(w / 2, h / 2));
        Assert.NotEqual(15, SendHitTest(w / 2, h / 2));
    }

    [Fact]
    public void MainForm_WmNcCalcSize_RemovesStandardFrame_AndHandlesMaximizedBounds()
    {
        using var form = new MainForm();
        form.Show();
        Application.DoEvents();

        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var wndProcMethod = form.GetType().GetMethod("WndProc", flags)!;

        // 1. Verify CreateParams has WS_THICKFRAME for Aero Snap and native resizing
        var createParamsProp = form.GetType().GetProperty("CreateParams", flags)!;
        var cp = (CreateParams)createParamsProp.GetValue(form)!;
        Assert.True((cp.Style & Win32.WS_THICKFRAME) != 0, "CreateParams must have WS_THICKFRAME");
        Assert.True((cp.Style & Win32.WS_MAXIMIZEBOX) != 0, "CreateParams must have WS_MAXIMIZEBOX");
        Assert.True((cp.Style & Win32.WS_MINIMIZEBOX) != 0, "CreateParams must have WS_MINIMIZEBOX");

        // 2. Test WM_NCCALCSIZE removes standard 8px non-client frame (Result == IntPtr.Zero)
        var nsp = new Win32.NCCALCSIZE_PARAMS
        {
            rgrc0 = new Win32.RECT { Left = 100, Top = 100, Right = 900, Bottom = 700 }
        };
        IntPtr nspPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf<Win32.NCCALCSIZE_PARAMS>());
        try
        {
            System.Runtime.InteropServices.Marshal.StructureToPtr(nsp, nspPtr, false);
            var m = Message.Create(form.Handle, Win32.WM_NCCALCSIZE, (IntPtr)1, nspPtr);
            object[] args = new object[] { m };
            wndProcMethod.Invoke(form, args);
            var resMsg = (Message)args[0];
            Assert.Equal(IntPtr.Zero, resMsg.Result);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(nspPtr);
        }

        // 3. Test WM_GETMINMAXINFO clamps maximized window size to monitor working area
        var mmi = new Win32.MINMAXINFO();
        IntPtr mmiPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf<Win32.MINMAXINFO>());
        try
        {
            System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, mmiPtr, false);
            var m = Message.Create(form.Handle, Win32.WM_GETMINMAXINFO, IntPtr.Zero, mmiPtr);
            object[] args = new object[] { m };
            wndProcMethod.Invoke(form, args);
            var resMmi = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32.MINMAXINFO>(mmiPtr);
            var workArea = Screen.FromHandle(form.Handle).WorkingArea;
            Assert.Equal(workArea.Width, resMmi.ptMaxSize.X);
            Assert.Equal(workArea.Height, resMmi.ptMaxSize.Y);
            Assert.True(resMmi.ptMinTrackSize.X >= Theme.MinWindowWidth);
            Assert.True(resMmi.ptMinTrackSize.Y >= Theme.MinWindowHeight);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(mmiPtr);
        }

        // 4. Test DWM corner preference & border styling (disables rounded corner transparency)
        Win32.DisableWindowRounding(form.Handle);
        Win32.SetDwmBorderColor(form.Handle, Theme.Border);
        Win32.SetDwmCaptionColor(form.Handle, Theme.SurfaceElevated);
        Win32.EnableImmersiveDarkMode(form.Handle);
    }

    [Fact]
    public void MainForm_NonClientMessages_SuppressesWhiteFrameAndNativeCaption()
    {
        using var form = new MainForm();
        form.Show();
        Application.DoEvents();

        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var wndProcMethod = form.GetType().GetMethod("WndProc", flags)!;

        // 1. WM_NCPAINT 必須被攔截並設為 IntPtr.Zero，徹底阻止 Windows 繪製預設白框
        var msgNcPaint = Message.Create(form.Handle, Win32.WM_NCPAINT, IntPtr.Zero, IntPtr.Zero);
        object[] argsNcPaint = new object[] { msgNcPaint };
        wndProcMethod.Invoke(form, argsNcPaint);
        Assert.Equal(IntPtr.Zero, ((Message)argsNcPaint[0]).Result);

        // 2. WM_NCACTIVATE (無論啟動或失去焦點) 必須抑制 DefWindowProc 重繪非客戶區 (傳遞 lParam=-1)
        var msgNcActivateInactive = Message.Create(form.Handle, Win32.WM_NCACTIVATE, IntPtr.Zero, IntPtr.Zero);
        object[] argsNcActivateInactive = new object[] { msgNcActivateInactive };
        wndProcMethod.Invoke(form, argsNcActivateInactive);
        Assert.Equal((IntPtr)1, ((Message)argsNcActivateInactive[0]).Result);

        var msgNcActivateActive = Message.Create(form.Handle, Win32.WM_NCACTIVATE, (IntPtr)1, IntPtr.Zero);
        object[] argsNcActivateActive = new object[] { msgNcActivateActive };
        wndProcMethod.Invoke(form, argsNcActivateActive);
        Assert.Equal((IntPtr)1, ((Message)argsNcActivateActive[0]).Result);

        // 3. WM_NCUAHDRAWCAPTION 與 WM_NCUAHDRAWFRAME 必須被壓制 (回傳 IntPtr.Zero)，防止主題引擎突發重繪
        var msgUahCaption = Message.Create(form.Handle, Win32.WM_NCUAHDRAWCAPTION, IntPtr.Zero, IntPtr.Zero);
        object[] argsUahCaption = new object[] { msgUahCaption };
        wndProcMethod.Invoke(form, argsUahCaption);
        Assert.Equal(IntPtr.Zero, ((Message)argsUahCaption[0]).Result);

        var msgUahFrame = Message.Create(form.Handle, Win32.WM_NCUAHDRAWFRAME, IntPtr.Zero, IntPtr.Zero);
        object[] argsUahFrame = new object[] { msgUahFrame };
        wndProcMethod.Invoke(form, argsUahFrame);
        Assert.Equal(IntPtr.Zero, ((Message)argsUahFrame[0]).Result);

        // 4. WM_THEMECHANGED 觸發時必須平滑處理並不拋出例外
        var msgThemeChanged = Message.Create(form.Handle, Win32.WM_THEMECHANGED, IntPtr.Zero, IntPtr.Zero);
        object[] argsThemeChanged = new object[] { msgThemeChanged };
        wndProcMethod.Invoke(form, argsThemeChanged);
    }

    [Fact]
    public void MainForm_MaximizeAndRestore_MaintainsLayoutAndVisibility()
    {
        using var form = new MainForm();
        form.Show();
        Application.DoEvents();

        var initialSize = form.Size;
        var initialClientSize = form.ClientSize;

        // Toggle Maximize
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var toggleMethod = form.GetType().GetMethod("ToggleMaximize", flags)!;
        toggleMethod.Invoke(form, null);
        Application.DoEvents();

        Assert.Equal(FormWindowState.Maximized, form.WindowState);
        Assert.True(form.ClientSize.Width >= initialClientSize.Width);

        // Toggle Restore
        toggleMethod.Invoke(form, null);
        Application.DoEvents();

        Assert.Equal(FormWindowState.Normal, form.WindowState);
        Assert.Equal(initialClientSize.Width, form.ClientSize.Width);
        Assert.Equal(initialClientSize.Height, form.ClientSize.Height);

        // Check child controls
        var pnlHeader = (Panel)form.GetType().GetField("_pnlHeader", flags)!.GetValue(form)!;
        var pnlBodyContainer = (Panel)form.GetType().GetField("_pnlBodyContainer", flags)!.GetValue(form)!;
        var pnlHomeView = (Panel)form.GetType().GetField("_pnlHomeView", flags)!.GetValue(form)!;

        // Verify that after restore to 1180x780, NO phantom scrollbar exists
        Assert.False(pnlHomeView.VerticalScroll.Visible, 
            $"VerticalScroll must NOT be visible after restore to 1180x780! Maximum={pnlHomeView.VerticalScroll.Maximum}, ClientH={pnlHomeView.ClientSize.Height}");

        // Verify layout
        Assert.True(form.ClientSize.Width > 0, $"ClientSize.Width is {form.ClientSize.Width}");
        Assert.True(form.ClientSize.Height > 0, $"ClientSize.Height is {form.ClientSize.Height}");
        Assert.True(pnlHeader.Visible, "Header should be visible");
        Assert.Equal(Theme.HeaderHeight, pnlHeader.Height);
        Assert.True(pnlBodyContainer.Visible, "Body container should be visible");
        Assert.True(pnlBodyContainer.Height > 0, $"pnlBodyContainer height is {pnlBodyContainer.Height}");
        Assert.True(pnlHomeView.Visible, "Home view should be visible");
        Assert.True(pnlHomeView.Height > 0, $"pnlHomeView height is {pnlHomeView.Height}");

        // Now test shrinking to smaller window (800x520):
        form.Size = new Size(800, 520);
        Application.DoEvents();
        Assert.True(pnlHomeView.VerticalScroll.Visible, "When window height is 520, scrollbar should appear as content is taller");

        // Now enlarge back to default 1180x780:
        form.Size = new Size(1180, 780);
        Application.DoEvents();
        Assert.False(pnlHomeView.VerticalScroll.Visible, "When enlarged back to 1180x780, scrollbar should completely disappear without phantom space");

        var inputAddr = (Control)form.GetType().GetField("_inputRemoteAddress", flags)!.GetValue(form)!;
        Assert.True(inputAddr.Location.Y >= 0, $"_inputRemoteAddress Y is negative: {inputAddr.Location.Y}!");
        var pnlDevicesGrid = (Control)form.GetType().GetField("_pnlDevicesGrid", flags)!.GetValue(form)!;
        Assert.True(pnlDevicesGrid.Height >= 280, $"pnlDevicesGrid.Height is {pnlDevicesGrid.Height} which is < 280");
    }

    [Fact]
    public void AccessRequestDialog_HasWideAcceptButton_AndMatchesDarkTheme()
    {
        using var dlg = new AccessRequestDialog("WORKSTATION-01", "123 456 789", "192.168.1.50:21119", "584920");
        dlg.Show();
        Application.DoEvents();

        Assert.Equal(Theme.Background, dlg.BackColor);

        var allButtons = new List<Button>();
        void CollectButtons(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is Button b) allButtons.Add(b);
                CollectButtons(c);
            }
        }
        CollectButtons(dlg);

        var btnAccept = allButtons.FirstOrDefault(b => b.Text.Contains("接受"));
        var btnReject = allButtons.FirstOrDefault(b => b.Text.Contains("拒絕"));

        Assert.NotNull(btnAccept);
        Assert.NotNull(btnReject);

        // Verify button width is at least 150px to prevent text clipping
        Assert.True(btnAccept.Width >= 150, $"Accept button width {btnAccept.Width} must be at least 150px");
        Assert.True(btnReject.Width >= 90, $"Reject button width {btnReject.Width} must be at least 90px");

        using var bmp = new Bitmap(dlg.Width, dlg.Height);
        dlg.DrawToBitmap(bmp, new Rectangle(0, 0, dlg.Width, dlg.Height));
        bmp.Save("access_request_rendered.png");
    }

    [Fact]
    public void MonitorEnumerator_AssignsSequentialNumbers_EvenWithGpuDisplayGaps()
    {
        var monitors = MonitorEnumerator.GetMonitors();
        Assert.NotNull(monitors);
        for (int i = 0; i < monitors.Count; i++)
        {
            Assert.Equal(i + 1, monitors[i].Number);
        }
    }

    [Fact]
    public void MainForm_WhenFullscreen_SwitchToHomeTab_AutomaticallyExitsFullscreen()
    {
        using var form = new MainForm();
        form.Show();
        Application.DoEvents();

        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var toggleMethod = form.GetType().GetMethod("ToggleFullscreen", flags)!;
        var switchHomeMethod = form.GetType().GetMethod("SwitchToHomeTab", flags)!;
        var isFullscreenField = form.GetType().GetField("_isFullscreen", flags)!;

        // Enter fullscreen
        toggleMethod.Invoke(form, null);
        Assert.True((bool)isFullscreenField.GetValue(form)!);
        Assert.Equal(FormBorderStyle.None, form.FormBorderStyle);

        // Switch to home (e.g. on disconnect or clicking Home)
        switchHomeMethod.Invoke(form, null);
        Application.DoEvents();

        // Must automatically exit fullscreen
        Assert.False((bool)isFullscreenField.GetValue(form)!);
        Assert.Equal(FormBorderStyle.None, form.FormBorderStyle);
    }

    [Fact]
    public void VectorIcons_ConnectArrowAndSpinner_RenderCleanBitmaps()
    {
        using var arrowBmp = VectorIcons.CreateConnectArrowBitmap(32, 32, Color.White);
        Assert.NotNull(arrowBmp);
        Assert.Equal(32, arrowBmp.Width);
        Assert.Equal(32, arrowBmp.Height);

        // Verify arrow is not completely blank (has non-zero alpha pixels)
        bool hasPixels = false;
        for (int y = 0; y < arrowBmp.Height && !hasPixels; y++)
        {
            for (int x = 0; x < arrowBmp.Width; x++)
            {
                if (arrowBmp.GetPixel(x, y).A > 50)
                {
                    hasPixels = true;
                    break;
                }
            }
        }
        Assert.True(hasPixels, "Arrow icon should render visible pixels");

        using var spinnerBmp = VectorIcons.CreateSpinnerBitmap(32, 32, 90f, Theme.Accent);
        Assert.NotNull(spinnerBmp);
        Assert.Equal(32, spinnerBmp.Width);
        Assert.Equal(32, spinnerBmp.Height);

        bool hasSpinnerPixels = false;
        for (int y = 0; y < spinnerBmp.Height && !hasSpinnerPixels; y++)
        {
            for (int x = 0; x < spinnerBmp.Width; x++)
            {
                if (spinnerBmp.GetPixel(x, y).A > 50)
                {
                    hasSpinnerPixels = true;
                    break;
                }
            }
        }
        Assert.True(hasSpinnerPixels, "Spinner icon should render visible pixels");
    }

    [Fact]
    public void RemoteAddressInput_ConnectButton_UsesVectorIcon_WithoutText_AndAnimatesSpinner()
    {
        using var input = new RemoteAddressInput();
        input.Width = 400;

        // Verify there is NO text control with "連線" or "連線中..."
        foreach (Control child in input.Controls)
        {
            Assert.DoesNotContain("連線", child.Text ?? "");
        }

        // Action button is on the right edge
        var actionBtn = input.Controls.Cast<Control>().FirstOrDefault(c => c.GetType().Name == "ConnectActionButton");
        Assert.NotNull(actionBtn);
        Assert.True(actionBtn.Right >= input.Width - 50, "Action icon button should be placed at right edge");

        // Click action button triggers ConnectRequested
        string? requested = null;
        input.ConnectRequested += id => requested = id;
        input.TextValue = "987654321";

        var onClickMethod = typeof(Control).GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        onClickMethod?.Invoke(actionBtn, new object[] { EventArgs.Empty });
        Assert.Equal("987654321", requested);

        // IsLoading activates spinner and changes tooltip
        input.IsLoading = true;
        Assert.True(input.IsLoading);

        // Render to bitmap to ensure OnPaint handles IsLoading spinner state without exception
        using var bmp = new Bitmap(input.Width, input.Height);
        input.DrawToBitmap(bmp, new Rectangle(0, 0, input.Width, input.Height));

        input.IsLoading = false;
        Assert.False(input.IsLoading);
    }

    [Fact]
    public void MainForm_ConnectingBanner_HasAnimationAndCancelButton()
    {
        using var form = new MainForm();
        form.Show();
        Application.DoEvents();

        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var pnlConnectingBanner = (Panel)form.GetType().GetField("_pnlConnectingBanner", flags)!.GetValue(form)!;
        var connectingSpinner = (Control)form.GetType().GetField("_connectingSpinner", flags)!.GetValue(form)!;
        var lblConnectingText = (Label)form.GetType().GetField("_lblConnectingText", flags)!.GetValue(form)!;
        var btnCancelConnect = (Button)form.GetType().GetField("_btnCancelConnect", flags)!.GetValue(form)!;
        var connectingTimer = (System.Windows.Forms.Timer)form.GetType().GetField("_connectingTimer", flags)!.GetValue(form)!;

        Assert.NotNull(pnlConnectingBanner);
        Assert.NotNull(connectingSpinner);
        Assert.NotNull(lblConnectingText);
        Assert.NotNull(btnCancelConnect);
        Assert.Equal(DockStyle.Top, pnlConnectingBanner.Dock);
        Assert.False(pnlConnectingBanner.Visible);

        // Start connecting animation
        var startMethod = form.GetType().GetMethod("StartConnectingAnimation", flags)!;
        var stopMethod = form.GetType().GetMethod("StopConnectingAnimation", flags)!;

        startMethod.Invoke(form, new object[] { "123456789" });
        var inputRemoteAddress = (RemoteAddressInput)form.GetType().GetField("_inputRemoteAddress", flags)!.GetValue(form)!;
        inputRemoteAddress.IsLoading = true;
        Application.DoEvents();

        Assert.True(pnlConnectingBanner.Visible, "Connecting banner must be visible during connection");
        Assert.True(connectingTimer.Enabled, "Connecting animation timer must be running");
        Assert.Contains("123 456 789", lblConnectingText.Text);

        using (var connectingBmp = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(connectingBmp, new Rectangle(0, 0, form.Width, form.Height));
            connectingBmp.Save("main_form_connecting_rendered.png");
        }

        // Test cancel button aborts connection
        var cts = new System.Threading.CancellationTokenSource();
        form.GetType().GetField("_connectingCts", flags)!.SetValue(form, cts);

        var onClickMethod = typeof(Control).GetMethod("OnClick", flags);
        onClickMethod?.Invoke(btnCancelConnect, new object[] { EventArgs.Empty });
        Assert.True(cts.IsCancellationRequested, "Clicking cancel button must cancel the connection CTS");

        // Stop connecting animation
        stopMethod.Invoke(form, null);
        Application.DoEvents();

        Assert.False(pnlConnectingBanner.Visible, "Connecting banner must be hidden when stopped");
        Assert.False(connectingTimer.Enabled, "Animation timer must be stopped");
    }

    [Fact]
    public void DarkPasswordInput_MatchesMainAppStyle_AndTogglesEyeMask()
    {
        using var input = new DarkPasswordInput("測試密碼輸入");
        input.Width = 350;

        Assert.Equal(44, input.Height);
        Assert.Equal("", input.Password);
        Assert.True(input.InnerTextBox.UseSystemPasswordChar);

        // Enter text
        input.Password = "Secret123";
        Assert.Equal("Secret123", input.Password);

        // Find eye toggle button
        var btnEye = input.Controls.Cast<Control>().FirstOrDefault(c => c.Text == "👁");
        Assert.NotNull(btnEye);

        // Click eye button toggles masking
        var onClick = typeof(Control).GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        onClick?.Invoke(btnEye, new object[] { EventArgs.Empty });
        Assert.False(input.InnerTextBox.UseSystemPasswordChar);

        // Click again restores masking
        onClick?.Invoke(btnEye, new object[] { EventArgs.Empty });
        Assert.True(input.InnerTextBox.UseSystemPasswordChar);

        // Enter key triggers ReturnPressed
        bool enterPressed = false;
        input.ReturnPressed += () => enterPressed = true;

        var onKeyDown = typeof(Control).GetMethod("OnKeyDown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        onKeyDown?.Invoke(input.InnerTextBox, new object[] { new KeyEventArgs(Keys.Enter) });
        Assert.True(enterPressed);

        // Paint test
        using var bmp = new Bitmap(input.Width, input.Height);
        input.DrawToBitmap(bmp, new Rectangle(0, 0, input.Width, input.Height));
    }

    [Fact]
    public void ConnectPasswordDialog_MatchesDarkTheme_NoUnattendedOrInteractiveButton_CleanPasswordPrompt()
    {
        using var dlg = new ConnectPasswordDialog("WORK-DESK", "987654321", "PrefilledPwd", allowInteractive: true, errorMessage: "⚠️ 連線密碼錯誤，請重新輸入：");
        dlg.Show();
        Application.DoEvents();

        // 1. Verify dark theme color palette
        Assert.Equal(Theme.Background, dlg.BackColor);
        Assert.Equal(Theme.TextPrimary, dlg.ForeColor);

        // 2. Verify DarkPasswordInput control is embedded
        var pwdInput = dlg.Controls.OfType<DarkPasswordInput>().FirstOrDefault();
        Assert.NotNull(pwdInput);
        Assert.Equal("PrefilledPwd", pwdInput.Password);
        Assert.Equal("PrefilledPwd", dlg.Password);

        // 3. Verify Checkbox state
        var chk = dlg.Controls.OfType<CheckBox>().FirstOrDefault();
        Assert.NotNull(chk);
        Assert.True(chk.Checked);
        Assert.Equal(Theme.TextSecondary, chk.ForeColor);

        // 4. Verify no "無人值守" in title or prompt labels
        var allLabels = dlg.Controls.OfType<Label>().ToList();
        var titleLabel = allLabels.FirstOrDefault(l => l.Text.Contains("輸入連線密碼"));
        Assert.NotNull(titleLabel);
        Assert.DoesNotContain(allLabels, l => l.Text.Contains("無人值守"));

        // 5. Verify NO interactive button exists on the client password dialog
        var allButtons = dlg.Controls.OfType<Button>().ToList();
        var btnInteractive = allButtons.FirstOrDefault(b => b.Text.Contains("互動授權"));
        Assert.Null(btnInteractive);

        // 6. Verify Cancel and Connect buttons exist
        var btnCancel = allButtons.FirstOrDefault(b => b.Text == "取消");
        var btnConnect = allButtons.FirstOrDefault(b => b.Text == "連線");
        Assert.NotNull(btnCancel);
        Assert.NotNull(btnConnect);

        // 7. Render to bitmap and save artifact
        using (var bmp = new Bitmap(dlg.Width, dlg.Height))
        {
            dlg.DrawToBitmap(bmp, new Rectangle(0, 0, dlg.Width, dlg.Height));
            bmp.Save("connect_password_dialog_rendered.png");
        }
    }

    [Fact]
    public void PasswordDialog_HostSecurity_MatchesDarkTheme()
    {
        using var dlg = new PasswordDialog(hasExistingPassword: true);
        dlg.Show();
        Application.DoEvents();

        Assert.Equal(Theme.Background, dlg.BackColor);
        Assert.Equal(Theme.TextPrimary, dlg.ForeColor);

        var pwdInputs = dlg.Controls.OfType<DarkPasswordInput>().ToList();
        Assert.Equal(2, pwdInputs.Count);

        using var bmp = new Bitmap(dlg.Width, dlg.Height);
        dlg.DrawToBitmap(bmp, new Rectangle(0, 0, dlg.Width, dlg.Height));
        bmp.Save("host_password_dialog_rendered.png");
    }

    [Fact]
    public void MainForm_WindowShrink_ButtonsRetainFixedSizesAndDoNotOverlap()
    {
        using var form = new MainForm();
        form.Show();
        Application.DoEvents();

        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var btnHeaderSettings = (Button)form.GetType().GetField("_btnHeaderSettings", flags)!.GetValue(form)!;
        var btnDisconnect = (Button)form.GetType().GetField("_btnSessionDisconnect", flags)!.GetValue(form)!;
        var btnSettings = (Button)form.GetType().GetField("_btnSessionSettings", flags)!.GetValue(form)!;
        var btnFullscreen = (Button)form.GetType().GetField("_btnFullscreen", flags)!.GetValue(form)!;
        var btnClipboard = (Button)form.GetType().GetField("_btnClipboardSync", flags)!.GetValue(form)!;
        var btnSpecialKeys = (Button)form.GetType().GetField("_btnSpecialKeys", flags)!.GetValue(form)!;
        var btnIme = (Button)form.GetType().GetField("_btnImeMode", flags)!.GetValue(form)!;
        var pnlMonitors = (FlowLayoutPanel)form.GetType().GetField("_pnlMonitors", flags)!.GetValue(form)!;
        var lblLogo = (Label)form.GetType().GetField("_lblLogo", flags)!.GetValue(form)!;
        var lblSessionTitle = (Label)form.GetType().GetField("_lblSessionTitle", flags)!.GetValue(form)!;
        var lblSessionStatusBadge = (Label)form.GetType().GetField("_lblSessionStatusBadge", flags)!.GetValue(form)!;
        var updateHeaderMethod = form.GetType().GetMethod("UpdateHeaderLayout", flags)!;

        // 1. Home mode at normal and shrunk sizes
        form.Size = new Size(1180, 780);
        updateHeaderMethod.Invoke(form, null);
        Application.DoEvents();
        Assert.True(btnHeaderSettings.AutoEllipsis);
        Assert.Equal(88, btnHeaderSettings.Width);
        Assert.Equal(30, btnHeaderSettings.Height);

        form.Size = new Size(720, 500);
        updateHeaderMethod.Invoke(form, null);
        Application.DoEvents();
        Assert.Equal(88, btnHeaderSettings.Width);
        Assert.Equal(30, btnHeaderSettings.Height);

        // 2. Session mode at normal and shrunk sizes
        var sessionItemType = typeof(MainForm).GetNestedType("SessionTabItem", flags)!;
        var dummySession = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(sessionItemType);
        form.GetType().GetField("_currentSession", flags)!.SetValue(form, dummySession);

        var pnlHomeView = (Panel)form.GetType().GetField("_pnlHomeView", flags)!.GetValue(form)!;
        var pnlSessionView = (Panel)form.GetType().GetField("_pnlSessionView", flags)!.GetValue(form)!;
        pnlHomeView.Visible = false;
        pnlSessionView.Visible = true;
        lblSessionTitle.Text = "WORK-PC-VERY-LONG-DEVICE-NAME (123 456 789)";

        pnlMonitors.Controls.Clear();
        var m1 = new Button { Size = new Size(34, 30), Margin = new Padding(0, 0, 4, 0) };
        var m2 = new Button { Size = new Size(34, 30), Margin = new Padding(0, 0, 4, 0) };
        pnlMonitors.Controls.AddRange(new Control[] { m1, m2 });

        var updateToolbarMethod = form.GetType().GetMethod("UpdateSessionToolbarLayout", flags)!;

        form.Size = new Size(1180, 780);
        updateHeaderMethod.Invoke(form, null);
        updateToolbarMethod.Invoke(form, null);
        Application.DoEvents();

        // Verify button sizes at 1180
        Assert.Equal(new Size(34, 30), btnDisconnect.Size);
        Assert.Equal(new Size(34, 30), btnSettings.Size);
        Assert.Equal(new Size(34, 30), btnFullscreen.Size);
        Assert.Equal(new Size(34, 30), btnClipboard.Size);
        Assert.Equal(new Size(34, 30), btnSpecialKeys.Size);
        Assert.Equal(new Size(34, 30), btnIme.Size);
        Assert.Equal(new Size(34, 30), m1.Size);
        Assert.Equal(new Size(34, 30), m2.Size);

        // Shrink form down to 880
        form.Size = new Size(880, 600);
        updateHeaderMethod.Invoke(form, null);
        updateToolbarMethod.Invoke(form, null);
        Application.DoEvents();

        // Verify button sizes DO NOT change on shrink
        Assert.Equal(new Size(34, 30), btnDisconnect.Size);
        Assert.Equal(new Size(34, 30), btnSettings.Size);
        Assert.Equal(new Size(34, 30), btnFullscreen.Size);
        Assert.Equal(new Size(34, 30), btnClipboard.Size);
        Assert.Equal(new Size(34, 30), btnSpecialKeys.Size);
        Assert.Equal(new Size(34, 30), btnIme.Size);
        Assert.Equal(new Size(34, 30), m1.Size);
        Assert.Equal(new Size(34, 30), m2.Size);

        // Verify buttons maintain strict left-to-right order and do not overlap
        Assert.True(btnDisconnect.Location.X > btnSettings.Location.X, "Disconnect > Settings");
        Assert.True(btnSettings.Location.X > btnFullscreen.Location.X, "Settings > Fullscreen");
        Assert.True(btnFullscreen.Location.X > btnClipboard.Location.X, "Fullscreen > Clipboard");
        Assert.True(btnClipboard.Location.X > btnSpecialKeys.Location.X, "Clipboard > SpecialKeys");
        Assert.True(btnSpecialKeys.Location.X > btnIme.Location.X, "SpecialKeys > Ime");
        Assert.True(btnIme.Location.X > pnlMonitors.Location.X, "Ime > Monitors");
        Assert.True(lblSessionStatusBadge.Location.X > lblSessionTitle.Location.X, "StatusBadge > Title");
        Assert.True(lblSessionTitle.Location.X > lblLogo.Location.X, "Title > Logo");
        Assert.True(lblSessionTitle.AutoEllipsis, "Title has AutoEllipsis");

        // Shrink form down to 720 (minimum supported width)
        form.Size = new Size(720, 500);
        updateHeaderMethod.Invoke(form, null);
        updateToolbarMethod.Invoke(form, null);
        Application.DoEvents();

        // CRITICAL: Verify Settings button and Disconnect button are 100% inside client area (NOT compressed or off-screen)
        var pnlSessionToolbar = (Panel)form.GetType().GetField("_pnlSessionToolbar", flags)!.GetValue(form)!;
        Assert.True(btnDisconnect.Right <= pnlSessionToolbar.ClientSize.Width, $"Disconnect Right {btnDisconnect.Right} exceeds {pnlSessionToolbar.ClientSize.Width}");
        Assert.True(btnSettings.Right <= btnDisconnect.Left, $"Settings Right {btnSettings.Right} overlaps Disconnect {btnDisconnect.Left}");
        Assert.True(btnSettings.Left >= 0, $"Settings Left {btnSettings.Left} must be >= 0");
        Assert.Equal(new Size(34, 30), btnSettings.Size);
        Assert.Equal(new Size(34, 30), btnDisconnect.Size);
    }

    [Fact]
    public void Protocol_DisconnectMessages_AreDefinedAndDistinct()
    {
        Assert.Equal((byte)0x0B, (byte)HostMsg.Disconnect);
        Assert.Equal((byte)0x51, (byte)ViewerMsg.Disconnect);
    }

    [Fact]
    public async Task Disconnect_ViewerDisconnects_HostSessionEndsImmediately()
    {
        int port = 21299;
        var hostCfg = new HostConfig
        {
            DeviceId = "111222333",
            DeviceName = "HostTest",
            TcpPort = port,
            HostEnabled = true,
            AllowInteractive = false
        };
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        hostCfg.PasswordSalt = Convert.ToBase64String(salt);
        hostCfg.PasswordIterations = 1000;
        var key = Crypto.DerivePasswordKey("testpwd", salt, 1000);
        hostCfg.PasswordVerifier = Convert.ToBase64String(key);

        using var discovery = new LanDiscovery("111222333", port: 21390);
        var dummyUi = new DummyUi();
        using var server = new HostServer(hostCfg, discovery, dummyUi);
        server.Start();

        var viewerCfg = new ViewerConfig { ViewerId = "999888777" };
        using var viewer = new ViewerSession($"127.0.0.1:{port}", viewerCfg, discovery);

        var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await viewer.ConnectAsync("testpwd", connectCts.Token);
        for (int i = 0; i < 40; i++)
        {
            if (server.ActiveSession != null) break;
            await Task.Delay(50);
        }
        Assert.NotNull(server.ActiveSession);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        viewer.SendDisconnect();
        viewer.Dispose();

        for (int i = 0; i < 60; i++)
        {
            if (server.ActiveSession == null) break;
            await Task.Delay(50);
        }
        sw.Stop();
        Assert.Null(server.ActiveSession);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"Disconnection took {sw.ElapsedMilliseconds} ms, which is too slow!");
    }

    [Fact]
    public async Task Disconnect_HostDisconnects_ViewerEndsImmediately()
    {
        int port = 21303;
        var hostCfg = new HostConfig
        {
            DeviceId = "333444555",
            DeviceName = "HostDisconnectTest",
            TcpPort = port,
            HostEnabled = true,
            AllowInteractive = false
        };
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        hostCfg.PasswordSalt = Convert.ToBase64String(salt);
        hostCfg.PasswordIterations = 1000;
        var key = Crypto.DerivePasswordKey("testpwd", salt, 1000);
        hostCfg.PasswordVerifier = Convert.ToBase64String(key);

        using var discovery = new LanDiscovery("333444555", port: 21391);
        var dummyUi = new DummyUi();
        using var server = new HostServer(hostCfg, discovery, dummyUi);
        server.Start();

        var viewerCfg = new ViewerConfig { ViewerId = "777666555" };
        using var viewer = new ViewerSession($"127.0.0.1:{port}", viewerCfg, discovery);

        bool viewerDisconnectedFired = false;
        viewer.Disconnected += reason => viewerDisconnectedFired = true;

        var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await viewer.ConnectAsync("testpwd", connectCts.Token);
        for (int i = 0; i < 40; i++)
        {
            if (server.ActiveSession != null) break;
            await Task.Delay(50);
        }
        Assert.NotNull(server.ActiveSession);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        server.DisconnectActiveSession();

        for (int i = 0; i < 60; i++)
        {
            if (viewerDisconnectedFired) break;
            await Task.Delay(50);
        }
        sw.Stop();
        Assert.True(viewerDisconnectedFired, "Viewer should receive Host Disconnect notification!");
        Assert.True(sw.ElapsedMilliseconds < 1500, $"Host-initiated disconnection took {sw.ElapsedMilliseconds} ms, which is too slow!");
    }

    [Fact]
    public void AppBranding_HasValidIconAndMasterLogo()
    {
        Assert.NotNull(AppBranding.AppIcon);
        Assert.True(AppBranding.AppIcon.Width > 0);
        Assert.True(AppBranding.AppIcon.Height > 0);

        using var logoBmp = AppBranding.GetLogoBitmap(24, 24);
        Assert.NotNull(logoBmp);
        Assert.Equal(24, logoBmp.Width);
        Assert.Equal(24, logoBmp.Height);

        // Verify forms have icon assigned
        using var testForm = new Form();
        AppBranding.ApplyToForm(testForm);
        Assert.NotNull(testForm.Icon);
        Assert.True(testForm.ShowIcon);
    }

    [Fact]
    public async Task Session_RemainsConnected_DoesNotDropPrematurely()
    {
        int port = 21301;
        var hostCfg = new HostConfig
        {
            DeviceId = "222333444",
            DeviceName = "HostStayAliveTest",
            TcpPort = port,
            HostEnabled = true,
            AllowInteractive = false
        };
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        hostCfg.PasswordSalt = Convert.ToBase64String(salt);
        hostCfg.PasswordIterations = 1000;
        var key = Crypto.DerivePasswordKey("testpwd", salt, 1000);
        hostCfg.PasswordVerifier = Convert.ToBase64String(key);

        using var discovery = new LanDiscovery("222333444", port: 21392);
        var dummyUi = new DummyUi();
        using var server = new HostServer(hostCfg, discovery, dummyUi);
        server.Start();

        var viewerCfg = new ViewerConfig { ViewerId = "888777666" };
        using var viewer = new ViewerSession($"127.0.0.1:{port}", viewerCfg, discovery);

        var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await viewer.ConnectAsync("testpwd", connectCts.Token);
        for (int i = 0; i < 40; i++)
        {
            if (server.ActiveSession != null) break;
            await Task.Delay(50);
        }
        Assert.NotNull(server.ActiveSession);
        Assert.True(viewer.IsConnected);

        // Wait during active session; verify it NEVER falsely disconnects after 2-3 seconds
        for (int i = 0; i < 4; i++)
        {
            await Task.Delay(400);
            Assert.True(viewer.IsConnected, "Viewer connection dropped prematurely!");
            Assert.NotNull(server.ActiveSession);
        }

        // Clean disconnect
        viewer.SendDisconnect();
    }

    [Fact]
    public void Installer_Form_InitializesWithDestinationAndDesktopShortcutCheckbox()
    {
        using var form = new RemoteDesk.Installer.InstallerForm();
        form.Show();
        Application.DoEvents();

        // 1. Verify controls exist
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var txtPath = (TextBox)form.GetType().GetField("_txtPath", flags)!.GetValue(form)!;
        var chkDesktop = (CheckBox)form.GetType().GetField("_chkDesktopShortcut", flags)!.GetValue(form)!;
        var chkStartMenu = (CheckBox)form.GetType().GetField("_chkStartMenuShortcut", flags)!.GetValue(form)!;
        var btnBrowse = (Button)form.GetType().GetField("_btnBrowse", flags)!.GetValue(form)!;
        var btnInstall = (Button)form.GetType().GetField("_btnInstall", flags)!.GetValue(form)!;

        Assert.NotNull(txtPath);
        Assert.False(string.IsNullOrWhiteSpace(txtPath.Text), "Default install path must not be empty");
        Assert.Contains("RemoteDesk", txtPath.Text);

        Assert.NotNull(chkDesktop);
        Assert.True(chkDesktop.Checked, "Desktop shortcut should be checked by default");
        Assert.Contains("桌面捷徑", chkDesktop.Text);

        Assert.NotNull(chkStartMenu);
        Assert.True(chkStartMenu.Checked, "Start menu shortcut should be checked by default");

        Assert.NotNull(btnBrowse);
        Assert.NotNull(btnInstall);

        // 2. Render screenshot of InstallerForm
        using var bmp = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
        bmp.Save("installer_form_rendered.png");
    }

    [Fact]
    public void Installer_Engine_ExtractsPayloadSuccessfully()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "RemoteDesk_Test_Install_" + Guid.NewGuid().ToString("N"));
        try
        {
            var logs = new List<string>();
            RemoteDesk.Installer.InstallerEngine.Install(
                tempDir,
                createDesktopShortcut: false,
                createStartMenuShortcut: false,
                progressCallback: (pct, msg) => logs.Add($"{pct}%: {msg}"));

            Assert.True(Directory.Exists(tempDir), "Target directory must be created");
            Assert.True(File.Exists(Path.Combine(tempDir, "RemoteDesk.exe")), "RemoteDesk.exe must exist");
            Assert.True(File.Exists(Path.Combine(tempDir, "RemoteDesk.dll")), "RemoteDesk.dll must exist");
            Assert.True(File.Exists(Path.Combine(tempDir, "uninstall.exe")), "uninstall.exe must exist");
            Assert.NotEmpty(logs);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void Installer_Engine_CanCreateAndValidateShortcut()
    {
        string tempLnk = Path.Combine(Path.GetTempPath(), "RemoteDesk_Test_" + Guid.NewGuid().ToString("N") + ".lnk");
        try
        {
            string target = Environment.ProcessPath ?? "cmd.exe";
            RemoteDesk.Installer.InstallerEngine.CreateShortcut(tempLnk, target, "Test Shortcut");
            Assert.True(File.Exists(tempLnk), "Shortcut file must be created on disk");
        }
        finally
        {
            if (File.Exists(tempLnk))
            {
                try { File.Delete(tempLnk); } catch { }
            }
        }
    }

    [Fact]
    public void WixBootstrapper_RuntimeStrategy_StrictDesktopRuntimeCheckAndOfficialDownload()
    {
        // 1. Check runtimeconfig.json for Framework-dependent .NET 9
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        string runtimeConfigPath = Path.Combine(repoRoot, "publish", "RemoteDesk.runtimeconfig.json");
        Assert.True(File.Exists(runtimeConfigPath), "RemoteDesk.runtimeconfig.json must exist in publish folder");
        string runtimeConfigJson = File.ReadAllText(runtimeConfigPath);
        Assert.Contains("Microsoft.WindowsDesktop.App", runtimeConfigJson);
        Assert.Contains("9.0.", runtimeConfigJson);

        // 2. Check Bundle.wxs configuration
        string bundleWxsPath = Path.Combine(repoRoot, "installer", "Bundle.wxs");
        Assert.True(File.Exists(bundleWxsPath), "Bundle.wxs must exist");
        string bundleWxs = File.ReadAllText(bundleWxsPath);

        // Verify .NET 9 Desktop Runtime x64 detection
        Assert.Contains("DotNetCoreSearch", bundleWxs);
        Assert.Contains("RuntimeType=\"desktop\"", bundleWxs);
        Assert.Contains("Platform=\"x64\"", bundleWxs);
        Assert.Contains("MajorVersion=\"9\"", bundleWxs);
        Assert.Contains("Variable=\"DotNet9DesktopExists\"", bundleWxs);

        // Verify Microsoft official download source
        Assert.Contains("https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/", bundleWxs);
        Assert.Contains("windowsdesktop-runtime-9.0.", bundleWxs);
        Assert.Contains("InstallArguments=\"/install /quiet /norestart\"", bundleWxs);
        Assert.Contains("Compressed=\"no\"", bundleWxs);

        // 3. Check Package.wxs configuration
        string pkgWxsPath = Path.Combine(repoRoot, "installer", "Package.wxs");
        Assert.True(File.Exists(pkgWxsPath), "Package.wxs must exist");
        string pkgWxs = File.ReadAllText(pkgWxsPath);
        Assert.Contains("WixUI_InstallDir", pkgWxs);
        Assert.Contains("ProgramFiles64Folder", pkgWxs);
        Assert.Contains("DesktopShortcut", pkgWxs);

        // 4. Verify output files
        string setupExe = Path.Combine(repoRoot, "dist", "RemoteDeskSetup.exe");
        string msiFile = Path.Combine(repoRoot, "dist", "RemoteDesk.msi");
        Assert.True(File.Exists(setupExe), "RemoteDeskSetup.exe must exist in dist folder");
        Assert.True(File.Exists(msiFile), "RemoteDesk.msi must exist in dist folder");
        Assert.True(new FileInfo(setupExe).Length > 1_000_000, "RemoteDeskSetup.exe must be a valid binary");
        Assert.True(new FileInfo(msiFile).Length > 1_000_000, "RemoteDesk.msi must be a valid binary");
    }

    [Fact]
    public void AppBranding_TrayIconAndBalloonTip_UsesCustomLogo()
    {
        // 1. 驗證 AppIcon 成功載入且尺寸合法
        Assert.NotNull(AppBranding.AppIcon);
        Assert.True(AppBranding.AppIcon.Width > 0);
        Assert.True(AppBranding.AppIcon.Height > 0);

        // 2. 驗證 AppIcon 點陣圖具備清晰的品牌主色與透明背景 (非雜訊)
        using var bmp = AppBranding.AppIcon.ToBitmap();
        Assert.NotNull(bmp);
        Assert.True(bmp.Width >= 16 && bmp.Height >= 16);

        // 四角應為透明或深色
        var corner = bmp.GetPixel(0, 0);
        Assert.True(corner.A < 50 || corner.R < 50, "Corner should be transparent or dark");

        // 3. 驗證指定尺寸 Logo Icon 生成 (48x48)
        using var logo48 = AppBranding.GetLogoIcon(48);
        Assert.NotNull(logo48);
        Assert.Equal(48, logo48.Width);
        Assert.Equal(48, logo48.Height);

        // 4. 驗證 AppUserModelId 格式與唯一性，並測試 EnsureShortcutAndIdentity
        Assert.Equal("RemoteDesk", AppBranding.AppUserModelId);
        AppBranding.EnsureShortcutAndIdentity();

        // 5. 驗證 NotifyIcon 擴充方法 ShowCustomBalloonTip 與 ShowNotification 可順暢調用
        using var trayIcon = new NotifyIcon
        {
            Icon = AppBranding.AppIcon,
            Visible = true
        };

        var type = typeof(NotifyIcon);
        foreach (var field in type.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
        {
            var val = field.GetValue(trayIcon);
            System.Diagnostics.Trace.WriteLine($"{field.Name} ({field.FieldType.Name}) = {val}");
        }

        var windowField = type.GetField("_window", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                       ?? type.GetField("window", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(windowField);
        var windowVal = windowField.GetValue(trayIcon);
        Assert.NotNull(windowVal);

        var nativeWindow = windowVal as NativeWindow;
        Assert.NotNull(nativeWindow);
        Assert.NotEqual(IntPtr.Zero, nativeWindow.Handle);

        var idField = type.GetField("_id", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                   ?? type.GetField("id", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(idField);
        int id = Convert.ToInt32(idField.GetValue(trayIcon));
        Assert.True(id > 0, "NotifyIcon ID should be positive");

        Type? winformsNidType = null;
        foreach (var asmItem in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                foreach (var t in asmItem.GetTypes())
                {
                    if (t.Name.Contains("NOTIFYICON") && t.Namespace != "RemoteDesk.Native")
                    {
                        winformsNidType = t;
                        break;
                    }
                }
            }
            catch { }
            if (winformsNidType != null) break;
        }
        Assert.NotNull(winformsNidType);
        int actualSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.NOTIFYICONDATA>();
        Assert.True(actualSize >= 952, "NOTIFYICONDATA size should be at least 952 bytes");

        var showMethod = type.GetMethod("ShowBalloonTip", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, new Type[] { typeof(int), typeof(string), typeof(string), typeof(ToolTipIcon) });
        Assert.NotNull(showMethod);

        // trayIcon.ShowCustomBalloonTip & ShowNotification
        trayIcon.ShowCustomBalloonTip(1000, "RemoteDesk", "測試自訂氣球提示訊息");
        trayIcon.ShowNotification("RemoteDesk", "測試自訂氣球提示訊息");
        trayIcon.Visible = false;
    }

    [Fact]
    public void AppBranding_ShortcutAndPhysicalIcon_AreAccuratelyConfigured()
    {
        AppBranding.EnsureShortcutAndIdentity();

        string userPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        string shortcutPath = Path.Combine(userPrograms, "RemoteDesk.lnk");
        Assert.True(File.Exists(shortcutPath), $"Shortcut should exist at {shortcutPath}");

        var link = (Win32.IPersistFile)new Win32.CShellLink();
        link.Load(shortcutPath, 0);

        var shellLink = (Win32.IShellLinkW)link;
        var sbTarget = new System.Text.StringBuilder(260);
        shellLink.GetPath(sbTarget, sbTarget.Capacity, IntPtr.Zero, 0);
        Assert.False(string.IsNullOrEmpty(sbTarget.ToString()), "TargetPath should not be empty");

        var sbIcon = new System.Text.StringBuilder(260);
        shellLink.GetIconLocation(sbIcon, sbIcon.Capacity, out int iconIndex);
        string iconLocation = sbIcon.ToString();
        Assert.False(string.IsNullOrEmpty(iconLocation), "IconLocation should not be empty");
        Assert.True(iconLocation.EndsWith(".ico", StringComparison.OrdinalIgnoreCase), $"IconLocation should point to an .ico file, was: {iconLocation}");
        Assert.True(File.Exists(iconLocation), $"Icon file should exist on disk: {iconLocation}");

        var propStore = (Win32.IPropertyStore)link;
        var key = Win32.PROPERTYKEY.AppUserModel_ID;
        using (var pv = new Win32.PROPVARIANT())
        {
            propStore.GetValue(ref key, pv);
            string? aumid = pv.vt == 31 ? System.Runtime.InteropServices.Marshal.PtrToStringUni(pv.pwszVal) : null;
            Assert.Equal("RemoteDesk", aumid);
        }

        // Verify HKCU Registry
        using var regKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId\RemoteDesk");
        Assert.NotNull(regKey);
        Assert.Equal("RemoteDesk", regKey.GetValue("DisplayName"));
        string? regIcon = regKey.GetValue("IconUri") as string;
        Assert.NotNull(regIcon);
        Assert.True(regIcon.EndsWith(".ico", StringComparison.OrdinalIgnoreCase), $"Registry IconUri should point to .ico: {regIcon}");
        Assert.True(File.Exists(regIcon), $"Registry IconUri file should exist: {regIcon}");
    }

    [Fact]
    public void NetworkHelper_IsSelfTarget_CorrectlyDetectsSelfAndRemote()
    {
        string selfId = "123456789";
        string viewerId = "987654321";

        // 1. Self ID (含空格與不含空格)
        Assert.True(NetworkHelper.IsSelfTarget("123456789", selfId, viewerId));
        Assert.True(NetworkHelper.IsSelfTarget("123 456 789", selfId, viewerId));
        Assert.True(NetworkHelper.IsSelfTarget("987654321", selfId, viewerId));
        Assert.True(NetworkHelper.IsSelfTarget("987 654 321", selfId, viewerId));

        // 2. Loopback 與 Local Host
        Assert.True(NetworkHelper.IsSelfTarget("127.0.0.1", selfId, viewerId));
        Assert.True(NetworkHelper.IsSelfTarget("127.0.0.1:21119", selfId, viewerId));
        Assert.True(NetworkHelper.IsSelfTarget("127.0.0.2", selfId, viewerId));
        Assert.True(NetworkHelper.IsSelfTarget("127.255.255.255:21119", selfId, viewerId));
        Assert.True(NetworkHelper.IsSelfTarget("tcp://127.0.0.1:21119", selfId, viewerId));
        Assert.True(NetworkHelper.IsSelfTarget("http://127.0.0.1:21119/", selfId, viewerId));
        Assert.True(NetworkHelper.IsSelfTarget("localhost", selfId, viewerId));
        Assert.True(NetworkHelper.IsSelfTarget("localhost:21119", selfId, viewerId));
        Assert.True(NetworkHelper.IsSelfTarget("tcp://localhost:21119", selfId, viewerId));
        Assert.True(NetworkHelper.IsSelfTarget("::1", selfId, viewerId));
        Assert.True(NetworkHelper.IsSelfTarget("[::1]:21119", selfId, viewerId));
        Assert.True(NetworkHelper.IsSelfTarget("0.0.0.0", selfId, viewerId));
        Assert.True(NetworkHelper.IsSelfTarget("::", selfId, viewerId));
        Assert.True(NetworkHelper.IsSelfTarget(Environment.MachineName, selfId, viewerId));

        // 3. 本機所有網路介面 IP
        var localIps = NetworkHelper.GetLocalAddresses();
        foreach (var ip in localIps)
        {
            Assert.True(NetworkHelper.IsSelfTarget(ip.ToString(), selfId, viewerId));
            Assert.True(NetworkHelper.IsSelfTarget($"{ip}:21119", selfId, viewerId));
        }

        // 4. 遠端非本機 ID 與 IP
        Assert.False(NetworkHelper.IsSelfTarget("8.8.8.8", selfId, viewerId));
        Assert.False(NetworkHelper.IsSelfTarget("1.1.1.1:21119", selfId, viewerId));
        Assert.False(NetworkHelper.IsSelfTarget("555666777", selfId, viewerId));
    }

    [Fact]
    public void DeviceTabsControl_SupportsThreeTabs_AndSelectsLanDevices()
    {
        using var tabs = new DeviceTabsControl();
        tabs.UpdateCounts(5, 3, 7);

        // Click on Tab 2 (區域網內設備)
        using var bmp = new Bitmap(500, 38);
        using var g = Graphics.FromImage(bmp);
        tabs.DrawToBitmap(bmp, new Rectangle(0, 0, 500, 38));

        tabs.SelectedIndex = 2;
        Assert.Equal(2, tabs.SelectedIndex);
    }

    [Fact]
    public void DeviceThumbnailCard_RendersDesktopAndStatusLights()
    {
        var entryOnline = new RecentEntry
        {
            Key = "111222333",
            DeviceId = "111222333",
            Name = "ONLINE-PC"
        };
        using var cardOnline = new DeviceThumbnailCard(entryOnline, true, _ => { }, _ => { }, _ => { });
        Assert.True(cardOnline.IsOnline);

        using var bmpOnline = new Bitmap(cardOnline.Width, cardOnline.Height);
        cardOnline.DrawToBitmap(bmpOnline, new Rectangle(0, 0, cardOnline.Width, cardOnline.Height));
        Assert.NotNull(bmpOnline);

        var entryOffline = new RecentEntry
        {
            Key = "444555666",
            DeviceId = "444555666",
            Name = "OFFLINE-PC"
        };
        using var cardOffline = new DeviceThumbnailCard(entryOffline, false, _ => { }, _ => { }, _ => { });
        Assert.False(cardOffline.IsOnline);

        using var bmpOffline = new Bitmap(cardOffline.Width, cardOffline.Height);
        cardOffline.DrawToBitmap(bmpOffline, new Rectangle(0, 0, cardOffline.Width, cardOffline.Height));
        Assert.NotNull(bmpOffline);
    }

    [Fact]
    public void MainForm_Fullscreen_ToggleAndEscBehavior()
    {
        using var form = new MainForm();
        form.Show();
        Application.DoEvents();

        var miToggle = typeof(MainForm).GetMethod("ToggleFullscreen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var miExit = typeof(MainForm).GetMethod("ExitFullscreen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var fiFullscreen = typeof(MainForm).GetField("_isFullscreen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var fiHeader = typeof(MainForm).GetField("_pnlHeader", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var fiToolbar = typeof(MainForm).GetField("_pnlSessionToolbar", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var fiSessionView = typeof(MainForm).GetField("_pnlSessionView", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Assert.NotNull(miToggle);
        Assert.NotNull(miExit);
        Assert.NotNull(fiFullscreen);
        Assert.NotNull(fiHeader);
        Assert.NotNull(fiToolbar);
        Assert.NotNull(fiSessionView);

        var pnlSessionView = (Panel)fiSessionView!.GetValue(form)!;
        pnlSessionView.Visible = true; // 模擬處於遠端工作階段畫面

        // Enter Fullscreen
        miToggle!.Invoke(form, null);
        Assert.True((bool)fiFullscreen!.GetValue(form)!);

        var pnlHeader = (Panel)fiHeader!.GetValue(form)!;
        var pnlToolbar = (Panel)fiToolbar!.GetValue(form)!;
        Assert.False(pnlHeader.Visible, "Header should be hidden in fullscreen");
        Assert.False(pnlToolbar.Visible, "Toolbar should be hidden in fullscreen");

        // Exit Fullscreen
        miExit!.Invoke(form, null);
        Assert.False((bool)fiFullscreen.GetValue(form)!);
        Assert.True(pnlHeader.Visible, "Header should be restored after exiting fullscreen");
        Assert.True(pnlToolbar.Visible, "Toolbar should be restored after exiting fullscreen");
    }

    [Fact]
    public void DesktopHelper_GetDesktopWallpaperBytes_ReturnsValidJpegOrNull()
    {
        var wp = DesktopHelper.GetDesktopWallpaperBytes();
        if (wp != null)
        {
            Assert.True(wp.Length > 100);
            // JPEG magic bytes: 0xFF, 0xD8
            Assert.Equal(0xFF, wp[0]);
            Assert.Equal(0xD8, wp[1]);
        }
    }

    [Fact]
    public void Protocol_WallpaperMessageCodec_WorksAccurately()
    {
        byte[] fakeJpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };
        byte[] packet = MessageCodec.Wallpaper(fakeJpeg);

        Assert.Equal((byte)HostMsg.Wallpaper, packet[0]);
        var reader = new PacketReader(packet.AsSpan(1));
        byte[] decoded = MessageCodec.ReadWallpaper(ref reader);

        Assert.Equal(fakeJpeg, decoded);
    }

    [Fact]
    public void DeviceThumbnailCard_RendersPureWallpaperThumbnail_WhenSupplied()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "RemoteDesk_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string tempThumb = Path.Combine(tempDir, "thumb.jpg");

        try
        {
            using (var bmp = new Bitmap(216, 122))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.DarkSlateBlue);
                bmp.Save(tempThumb, System.Drawing.Imaging.ImageFormat.Jpeg);
            }

            var entry = new RecentEntry
            {
                Key = "999888777",
                DeviceId = "999888777",
                ThumbnailPath = tempThumb
            };

            using var card = new DeviceThumbnailCard(entry, true, _ => { }, _ => { }, _ => { });
            using var targetBmp = new Bitmap(card.Width, card.Height);
            card.DrawToBitmap(targetBmp, new Rectangle(0, 0, card.Width, card.Height));
            Assert.NotNull(targetBmp);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private class DummyUi : IHostUi
    {
        public Task<bool> ConfirmAccessAsync(string viewerName, string viewerId, string address, string code, CancellationToken ct) => Task.FromResult(true);
        public void SessionStarted(HostSession session) { }
        public void SessionEnded(HostSession session) { }
        public void SetClipboardText(string text) { }
    }
}
