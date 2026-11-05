using System.Runtime.InteropServices;
using RemoteDesk.Common;
using RemoteDesk.Host;
using RemoteDesk.Native;

namespace RemoteDesk.Viewer;

/// <summary>
/// 控制端輸入法同步管理員：
/// 實現「兩端輸入法狀態綁定，不出現一中一英」的核心需求。
/// 支援 IMM32 / TSF 狀態雙向監聽與同步，並支援兩端鍵盤配置切換。
/// </summary>
internal sealed class ImeSyncClient : IDisposable
{
    private readonly Control _owner;
    private readonly Action<ImeState> _sendImeSet;
    private readonly Action<string> _sendText;
    private readonly System.Windows.Forms.Timer _pollTimer;

    private ImeSyncMode _mode = ImeSyncMode.Bound;
    private ImeState _currentState;
    private long _suppressUntil;
    private bool _disposed;

    public event Action<ImeState>? StateChanged;

    public ImeSyncMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            if (_mode == ImeSyncMode.Bound)
            {
                // 進入綁定模式時，將目前的輸入法狀態立即同步給遠端
                var s = GetLocalImeState();
                if (s.HasValue)
                {
                    _currentState = s.Value;
                    _sendImeSet(s.Value);
                    StateChanged?.Invoke(s.Value);
                }
            }
        }
    }

    public ImeState CurrentState => _currentState;

    public ImeSyncClient(Control owner, Action<ImeState> sendImeSet, Action<string> sendText)
    {
        _owner = owner;
        _sendImeSet = sendImeSet;
        _sendText = sendText;

        _pollTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();
    }

    /// <summary>
    /// 定期檢測本機輸入法狀態變化（例如使用者在本機按下 Shift 切換中/英或切換輸入法）。
    /// </summary>
    private void OnPollTick(object? sender, EventArgs e)
    {
        if (_disposed || !_owner.Focused) return;
        if (Environment.TickCount64 < _suppressUntil) return;

        var local = GetLocalImeState();
        if (!local.HasValue) return;

        var s = local.Value;
        if (!s.SameMode(_currentState) || s.KeyboardLayout != _currentState.KeyboardLayout)
        {
            _currentState = s;
            StateChanged?.Invoke(s);

            if (_mode == ImeSyncMode.Bound)
            {
                // 送往遠端，使遠端立即同步本機的輸入法中/英及語言
                _sendImeSet(s);
            }
        }
    }

    /// <summary>
    /// 收到遠端主機傳來的輸入法狀態變更（例如遠端切換視窗或遠端輸入法模式變更）。
    /// 將本機目前焦點控制項的輸入法強制設為相同狀態！
    /// </summary>
    public void OnRemoteImeStateReceived(ImeState remoteState)
    {
        if (_disposed) return;
        if (_mode != ImeSyncMode.Bound) return;

        if (_owner.InvokeRequired)
        {
            _owner.BeginInvoke(new Action(() => OnRemoteImeStateReceived(remoteState)));
            return;
        }

        // 避免本機回傳造成反彈振盪
        _suppressUntil = Environment.TickCount64 + 400;
        _currentState = remoteState;
        StateChanged?.Invoke(remoteState);

        ApplyToLocal(remoteState);
    }

    /// <summary>
    /// 強制將本機視窗輸入法設為目標狀態。
    /// </summary>
    private void ApplyToLocal(ImeState target)
    {
        var hwnd = _owner.Handle;
        if (hwnd == IntPtr.Zero) return;

        var tid = Win32.GetCurrentThreadId();
        var curHkl = Win32.GetKeyboardLayout(tid).ToInt64();

        // 1. 若鍵盤語系/佈局不同，請求切換本機鍵盤配置
        if ((curHkl & 0xFFFF) != target.LangId || (curHkl != target.KeyboardLayout))
        {
            var hkl = ImeController.FindLayout(target);
            if (hkl != IntPtr.Zero && hkl.ToInt64() != curHkl)
            {
                Win32.PostMessage(hwnd, Win32.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
            }
        }

        // 2. 設定 IMM32 開啟狀態與轉換模式 (中/英)
        var hImc = Win32.ImmGetContext(hwnd);
        if (hImc != IntPtr.Zero)
        {
            try
            {
                Win32.ImmSetOpenStatus(hImc, target.Open);
                if (target.Open)
                {
                    Win32.ImmSetConversionStatus(hImc, target.Conversion, 0);
                }
            }
            finally
            {
                Win32.ImmReleaseContext(hwnd, hImc);
            }
        }

        // 3. 亦向預設 IME 視窗發送控制指令 (確保相容 Win10/Win11 微軟新注音/拼音)
        var defIme = Win32.ImmGetDefaultIMEWnd(hwnd);
        if (defIme != IntPtr.Zero)
        {
            Win32.SendMessage(defIme, Win32.WM_IME_CONTROL, (IntPtr)Win32.IMC_SETOPENSTATUS, (IntPtr)(target.Open ? 1 : 0));
            if (target.Open)
            {
                Win32.SendMessage(defIme, Win32.WM_IME_CONTROL, (IntPtr)Win32.IMC_SETCONVERSIONMODE, (IntPtr)target.Conversion);
            }
        }
    }

    /// <summary>
    /// 讀取本機目前的輸入法狀態。
    /// </summary>
    public ImeState? GetLocalImeState()
    {
        var hwnd = _owner.Handle;
        if (hwnd == IntPtr.Zero) return null;

        var tid = Win32.GetCurrentThreadId();
        var hkl = Win32.GetKeyboardLayout(tid);

        var hImc = Win32.ImmGetContext(hwnd);
        bool open = false;
        int conv = 0;
        if (hImc != IntPtr.Zero)
        {
            try
            {
                open = Win32.ImmGetOpenStatus(hImc);
                Win32.ImmGetConversionStatus(hImc, out conv, out _);
            }
            finally
            {
                Win32.ImmReleaseContext(hwnd, hImc);
            }
        }

        return new ImeState(hkl.ToInt64(), open, conv);
    }

    /// <summary>
    /// 更新本機輸入法候選視窗與游標位置（針對本地輸入法模式）。
    /// </summary>
    public void UpdateCaretPosition(int screenX, int screenY, int lineHeight)
    {
        if (_mode != ImeSyncMode.Local) return;

        var hwnd = _owner.Handle;
        if (hwnd == IntPtr.Zero) return;

        var pt = new Win32.POINT { X = screenX, Y = screenY };
        var hImc = Win32.ImmGetContext(hwnd);
        if (hImc != IntPtr.Zero)
        {
            try
            {
                var comp = new Win32.COMPOSITIONFORM
                {
                    dwStyle = Win32.CFS_POINT,
                    ptCurrentPos = pt
                };
                Win32.ImmSetCompositionWindow(hImc, ref comp);

                var cand = new Win32.CANDIDATEFORM
                {
                    dwIndex = 0,
                    dwStyle = Win32.CFS_CANDIDATEPOS,
                    ptCurrentPos = pt
                };
                Win32.ImmSetCandidateWindow(hImc, ref cand);
            }
            finally
            {
                Win32.ImmReleaseContext(hwnd, hImc);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Stop();
        _pollTimer.Dispose();
    }
}
