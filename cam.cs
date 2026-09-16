using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;

class Program
{
    // =========================================================================
    // 1. 核心参数与状态控制
    // =========================================================================
    private static readonly string LogFilePath = @"D:\Remote_Activity_Log.txt";

    // 判定为常规视频推流的速率阈值 (KB/s)
    private const double VIDEO_STREAM_THRESHOLD_KB = 120.0;
    // 持续推流满 2 秒才触发插U盘音效
    private const double MIN_STREAM_DURATION_SECONDS = 2.0;
    // 流量中断超过该时长判定为断开 (秒)
    private const double STOP_TOLERANCE_SECONDS = 2.0;

    // 全局监听开关 (默认开启)
    private static volatile bool _isMonitoring = true;

    // =========================================================================
    // 2. Win32 系统音效 & 窗口控制
    // ==========================================
    [DllImport("winmm.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_ALIAS = 0x00010000;

    static void PlayUsbConnect() => PlaySound("DeviceConnect", IntPtr.Zero, SND_ALIAS | SND_ASYNC);
    static void PlayUsbDisconnect() => PlaySound("DeviceDisconnect", IntPtr.Zero, SND_ALIAS | SND_ASYNC);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_HIDE = 0;

    // =========================================================================
    // 3. 原生任务栏托盘与伪装控制面板菜单
    // =========================================================================
    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 1;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_NULL = 0x0000;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_CHECKED = 0x00000008;
    private const uint MF_UNCHECKED = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;

    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private static WndProcDelegate _wndProc;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lpTPMParams);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    // =========================================================================
    // 4. 原版 I/O 速率监测 API
    // =========================================================================
    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // =========================================================================
    // 5. WASAPI 音频输出监听 (对方开麦说话外放)
    // =========================================================================
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IMMDeviceEnumerator ppv);

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate([In] ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr AudioSessionGuid, int StreamFlags, out IntPtr SessionControl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr AudioSessionGuid, int StreamFlags, out IntPtr AudioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
    }

    [ComImport, Guid("E2F56580-1476-4EC6-A0DC-05D77DE16D77"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int SessionCount);
        [PreserveSig] int GetSession(int SessionIndex, out IAudioSessionControl2 Session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c23"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int pRetVal);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, [In] ref Guid EventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, [In] ref Guid EventContext);
        [PreserveSig] int GetGroupingParam(out Guid pRetVal);
        [PreserveSig] int SetGroupingParam([In] ref Guid Override, [In] ref Guid EventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr NewNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr NewNotifications);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int GetProcessId(out uint pRetVal);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    // =========================================================================
    // 6. 业务状态容器
    // =========================================================================
    private static readonly Dictionary<int, (ulong LastBytes, DateTime LastTime)> _processIoHistory = new();

    private class StreamState
    {
        public DateTime StartTime;
        public DateTime LastHighTraffic;
        public bool HasAlerted;
    }

    private static readonly Dictionary<int, StreamState> _streamStates = new();
    private static readonly HashSet<uint> _activeSpeakerPids = new();
    private static readonly HashSet<int> _knownRtcPids = new(); // 追踪 rtcRemoteDesktop.exe 进程
    private static NOTIFYICONDATA _nid;

    static void Main()
    {
        // 自动隐藏黑框静默运行
        IntPtr hConsole = GetConsoleWindow();
        if (hConsole != IntPtr.Zero) ShowWindow(hConsole, SW_HIDE);

        CoInitializeEx(IntPtr.Zero, 0);

        // 启动监控工作后台线程
        Thread workerThread = new(MonitoringWorkerLoop) { IsBackground = true };
        workerThread.Start();

        // 主线程运行托盘伪装与菜单循环
        RunTrayIconMessageLoop();
    }

    /// <summary>
    /// 后台监控主循环（受 _isMonitoring 开关控制）
    /// </summary>
    static void MonitoringWorkerLoop()
    {
        CoInitializeEx(IntPtr.Zero, 0);

        while (true)
        {
            if (_isMonitoring)
            {
                try
                {
                    Process[] processes = Process.GetProcesses();

                    // 1. 关键远控进程秒级识别 (rtcRemoteDesktop.exe)
                    MonitorSpecificRtcProcess(processes);

                    // 2. 原版带 2 秒持续判定的推流监控
                    MonitorDataStreamingWithTimer(processes);

                    // 3. 原版远控声音外放监控
                    MonitorRemoteVoicePlayback();
                }
                catch { }
            }

            Thread.Sleep(300); // 300ms 采样周期
        }
    }

    /// <summary>
    /// 专属检测：一旦 rtcRemoteDesktop.exe 进程启动，必定被监听，立即报提示
    /// </summary>
    static void MonitorSpecificRtcProcess(Process[] processes)
    {
        var currentRtcPids = new HashSet<int>();

        foreach (var p in processes)
        {
            try
            {
                // 不区分大小写匹配 rtcRemoteDesktop
                if (p.ProcessName.Equals("rtcRemoteDesktop", StringComparison.OrdinalIgnoreCase))
                {
                    int pid = p.Id;
                    currentRtcPids.Add(pid);

                    // 首次发现该进程启动，立即秒响
                    if (!_knownRtcPids.Contains(pid))
                    {
                        WriteLog($"[时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}] [严重警报] 核心远控进程已启动: rtcRemoteDesktop.exe (PID: {pid})，当前处于绝对被监听状态！");
                        PlayUsbConnect(); // 立即触发插U盘音效
                    }
                }
            }
            catch { }
        }

        // 检测退出事件：若之前记录的 PID 当前已不复存在，说明远控断开
        foreach (int pid in _knownRtcPids)
        {
            if (!currentRtcPids.Contains(pid))
            {
                WriteLog($"[时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}] [恢复安全] 核心远控进程已退出: rtcRemoteDesktop.exe (PID: {pid})");
                PlayUsbDisconnect(); // 触发拔出U盘音效
            }
        }

        _knownRtcPids.Clear();
        foreach (int pid in currentRtcPids)
        {
            _knownRtcPids.Add(pid);
        }
    }

    /// <summary>
    /// 原版：带计时器的流量检测（持续 2 秒防误报）
    /// </summary>
    static void MonitorDataStreamingWithTimer(Process[] processes)
    {
        DateTime now = DateTime.Now;
        var activePidsInSystem = new HashSet<int>();

        foreach (var p in processes)
        {
            int pid = p.Id;
            if (pid <= 4) continue;
            activePidsInSystem.Add(pid);

            // rtcRemoteDesktop 已有专属高优先级秒级接管，在此跳过，防止重复响铃
            if (p.ProcessName.Equals("rtcRemoteDesktop", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero) continue;

            try
            {
                if (GetProcessIoCounters(hProcess, out IO_COUNTERS io))
                {
                    ulong currentBytes = io.WriteTransferCount;

                    if (_processIoHistory.TryGetValue(pid, out var lastRecord))
                    {
                        double elapsedSeconds = (now - lastRecord.LastTime).TotalSeconds;
                        if (elapsedSeconds > 0.1)
                        {
                            double speedKBps = ((currentBytes - lastRecord.LastBytes) / 1024.0) / elapsedSeconds;

                            // 流量超过推流门限
                            if (speedKBps >= VIDEO_STREAM_THRESHOLD_KB)
                            {
                                if (!_streamStates.TryGetValue(pid, out StreamState state))
                                {
                                    _streamStates[pid] = new StreamState
                                    {
                                        StartTime = now,
                                        LastHighTraffic = now,
                                        HasAlerted = false
                                    };
                                }
                                else
                                {
                                    state.LastHighTraffic = now;

                                    // 核心逻辑：持续超标 >= 2.0 秒且尚未报警，才响铃！
                                    if (!state.HasAlerted && (now - state.StartTime).TotalSeconds >= MIN_STREAM_DURATION_SECONDS)
                                    {
                                        state.HasAlerted = true;
                                        string procPath = GetProcessPath(hProcess, pid);
                                        WriteLog($"[时间: {now:yyyy-MM-dd HH:mm:ss}] [确认拉流] 进程连续传输超过 2 秒: {procPath}, 瞬时速率: {speedKBps:F1} KB/s");
                                        PlayUsbConnect(); // 触发：插入U盘音效
                                    }
                                }
                            }
                        }
                    }

                    _processIoHistory[pid] = (currentBytes, now);
                }
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        // 处理超时与停止
        var toRemove = new List<int>();
        foreach (var kvp in _streamStates)
        {
            int pid = kvp.Key;
            StreamState state = kvp.Value;

            bool isProcessDead = !activePidsInSystem.Contains(pid);
            bool isTrafficStopped = (now - state.LastHighTraffic).TotalSeconds > STOP_TOLERANCE_SECONDS;

            if (isProcessDead || isTrafficStopped)
            {
                if (state.HasAlerted)
                {
                    WriteLog($"[时间: {now:yyyy-MM-dd HH:mm:ss}] [停止拉流] PID: {pid} 已停止持续推流");
                    PlayUsbDisconnect(); // 触发：拔出U盘音效
                }
                toRemove.Add(pid);
            }
        }

        foreach (int pid in toRemove)
        {
            _streamStates.Remove(pid);
        }
    }

    /// <summary>
    /// 原版：扬声器输出检测（对方远端开麦讲话）
    /// </summary>
    static void MonitorRemoteVoicePlayback()
    {
        IMMDeviceEnumerator enumerator = null;
        IMMDevice speakerDevice = null;
        IAudioSessionManager2 sessionManager = null;
        IAudioSessionEnumerator sessionEnum = null;

        var currentPids = new HashSet<uint>();

        try
        {
            int hr = CoCreateInstance(CLSID_MMDeviceEnumerator, IntPtr.Zero, 1, IID_IMMDeviceEnumerator, out enumerator);
            if (hr != 0 || enumerator == null) return;

            hr = enumerator.GetDefaultAudioEndpoint(0, 0, out speakerDevice);
            if (hr != 0 || speakerDevice == null) return;

            Guid iidMgr = IID_IAudioSessionManager2;
            hr = speakerDevice.Activate(ref iidMgr, 23, IntPtr.Zero, out object sessionManagerObj);
            if (hr != 0 || sessionManagerObj == null) return;

            sessionManager = (IAudioSessionManager2)sessionManagerObj;
            hr = sessionManager.GetSessionEnumerator(out sessionEnum);
            if (hr != 0 || sessionEnum == null) return;

            sessionEnum.GetCount(out int count);

            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl2 session = null;
                try
                {
                    sessionEnum.GetSession(i, out session);
                    if (session == null) continue;

                    session.GetState(out int state);
                    if (state == 1)
                    {
                        session.GetProcessId(out uint pid);
                        if (pid > 0)
                        {
                            currentPids.Add(pid);

                            if (!_activeSpeakerPids.Contains(pid))
                            {
                                string procName = GetProcessNameOnly((int)pid);
                                WriteLog($"[时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}] [语音上线] 进程正在向扬声器发声 (对方在说话): {procName} (PID: {pid})");
                                _activeSpeakerPids.Add(pid);
                            }
                        }
                    }
                }
                finally
                {
                    if (session != null) Marshal.ReleaseComObject(session);
                }
            }
        }
        finally
        {
            if (sessionEnum != null) Marshal.ReleaseComObject(sessionEnum);
            if (sessionManager != null) Marshal.ReleaseComObject(sessionManager);
            if (speakerDevice != null) Marshal.ReleaseComObject(speakerDevice);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }

        _activeSpeakerPids.RemoveWhere(pid => !currentPids.Contains(pid));
    }

    // =========================================================================
    // 7. 托盘初始化与左/右键菜单事件响应
    // =========================================================================
    static void RunTrayIconMessageLoop()
    {
        string className = "ControlPanelTrayMsgWindow";
        _wndProc = WndProc;

        WNDCLASSEX wc = new()
        {
            cbSize = Marshal.SizeOf(typeof(WNDCLASSEX)),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            lpszClassName = className
        };
        RegisterClassEx(ref wc);

        IntPtr hWnd = CreateWindowEx(0, className, "ControlPanelProxy", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hWnd == IntPtr.Zero) return;

        // 提取系统 control.exe 图标实现完美伪装
        IntPtr hIcon = IntPtr.Zero;
        string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string controlPath = Path.Combine(sysDir, "control.exe");
        if (File.Exists(controlPath))
        {
            ExtractIconEx(controlPath, 0, out _, out hIcon, 1);
        }
        if (hIcon == IntPtr.Zero)
        {
            ExtractIconEx("shell32.dll", 21, out _, out hIcon, 1);
        }

        _nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
            hWnd = hWnd,
            uID = 1001,
            uFlags = (int)(NIF_MESSAGE | NIF_ICON | NIF_TIP),
            uCallbackMessage = WM_TRAYICON,
            hIcon = hIcon,
            szTip = "控制面板"
        };
        Shell_NotifyIcon(NIM_ADD, ref _nid);

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        Shell_NotifyIcon(NIM_DELETE, ref _nid);
    }

    static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAYICON)
        {
            uint mouseMsg = (uint)lParam.ToInt64() & 0xFFFF;
            if (mouseMsg == WM_LBUTTONUP || mouseMsg == WM_RBUTTONUP)
            {
                ShowControlPopupMenu(hWnd);
            }
            return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    static void ShowControlPopupMenu(IntPtr hWnd)
    {
        IntPtr hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        // 选项1：开始监听
        uint startFlags = MF_STRING | (_isMonitoring ? MF_CHECKED : MF_UNCHECKED);
        AppendMenu(hMenu, startFlags, (UIntPtr)101, "开始监听");

        // 选项2：暂停监听
        uint pauseFlags = MF_STRING | (!_isMonitoring ? MF_CHECKED : MF_UNCHECKED);
        AppendMenu(hMenu, pauseFlags, (UIntPtr)102, "暂停监听");

        // 分割线与退出项
        AppendMenu(hMenu, MF_SEPARATOR, UIntPtr.Zero, string.Empty);
        AppendMenu(hMenu, MF_STRING, (UIntPtr)999, "退出");

        GetCursorPos(out POINT pt);
        SetForegroundWindow(hWnd);

        int selectedId = TrackPopupMenuEx(hMenu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, hWnd, IntPtr.Zero);
        PostMessage(hWnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

        DestroyMenu(hMenu);

        if (selectedId == 101)
        {
            _isMonitoring = true;
            WriteLog($"[时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}] [用户切换] 已切换为: 开始监听");
        }
        else if (selectedId == 102)
        {
            _isMonitoring = false;
            // 暂停时清空追踪缓冲，避免重新开始时误触发
            _streamStates.Clear();
            _processIoHistory.Clear();
            _activeSpeakerPids.Clear();
            _knownRtcPids.Clear();
            WriteLog($"[时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}] [用户切换] 已切换为: 暂停监听");
        }
        else if (selectedId == 999)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            DestroyWindow(hWnd);
            PostQuitMessage(0);
            Environment.Exit(0);
        }
    }

    // =========================================================================
    // 8. 辅助函数
    // =========================================================================
    static string GetProcessPath(IntPtr hProcess, int pid)
    {
        var sb = new StringBuilder(1024);
        int size = sb.Capacity;
        if (QueryFullProcessImageName(hProcess, 0, sb, ref size))
        {
            return sb.ToString();
        }
        return GetProcessNameOnly(pid);
    }

    static string GetProcessNameOnly(int pid)
    {
        try
        {
            return Process.GetProcessById(pid).ProcessName + ".exe";
        }
        catch
        {
            return $"PID_{pid}";
        }
    }

    static void WriteLog(string content)
    {
        try
        {
            File.AppendAllText(LogFilePath, content + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }
}
