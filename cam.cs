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
    // 动态时间配置 (可通过托盘菜单实时切换)
    // =========================================================================
    private static double _minStreamDurationSeconds = 2.5; // 默认 2.5 秒
    private const double TRAFFIC_THRESHOLD_KB = 80.0;       // 视频流速率门限 (KB/s)
    private const double STOP_TOLERANCE_SECONDS = 1.5;      // 停止容差时间 (秒)
    private static readonly string LogFilePath = @"D:\Remote_Activity_Log.txt";

    // 浏览器隔离名单
    private static readonly HashSet<string> ExcludedBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "opera", "brave", "360se", "360chrome",
        "qqbrowser", "sogouexplorer", "2345explorer", "liebao", "maxthon",
        "msedgewebview2", "conhost"
    };

    // =========================================================================
    // Win32 常量与结构体定义
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
    private static WndProcDelegate _wndProc; // 保持静态引用，防止 GC 回收委托

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

    // Win32 API
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

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);

    private const uint SOUND_FLAGS = 0x00210001; // SND_ASYNC | SND_ALIAS | SND_SYSTEM

    static void PlayUsbConnect()
    {
        if (!PlaySound("DeviceConnect", IntPtr.Zero, SOUND_FLAGS))
            MessageBeep(0x00000040);
    }

    static void PlayUsbDisconnect()
    {
        if (!PlaySound("DeviceDisconnect", IntPtr.Zero, SOUND_FLAGS))
            MessageBeep(0x00000030);
    }

    // =========================================================================
    // 内核网络 & IO API
    // =========================================================================
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int TableClass, uint Reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize, bool bOrder, int ulAf, int TableClass, uint Reserved);

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    // =========================================================================
    // WASAPI COM 接口定义
    // =========================================================================
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        IntPtr pUnkOuter, uint dwClsContext,
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
    // 业务状态管理
    // =========================================================================
    private class NetworkStreamTracker
    {
        public DateTime FirstSurgeTime;
        public DateTime LastSurgeTime;
        public ulong LastBytes;
        public DateTime LastSampleTime;
        public bool HasAlerted;
        public string ProcessFullPath;
        public string ProcessName;
    }

    private static readonly Dictionary<int, NetworkStreamTracker> _trackedStreams = new();
    private static readonly HashSet<uint> _activeSpeakerPids = new();
    private static NOTIFYICONDATA _nid;
    private static readonly double[] DurationOptions = new double[] { 1.0, 1.5, 2.0, 2.5, 3.0, 3.5, 4.0, 4.5, 5.0 };

    static void Main()
    {
        // 1. 静默运行：自动隐藏控制台黑框
        IntPtr hConsole = GetConsoleWindow();
        if (hConsole != IntPtr.Zero) ShowWindow(hConsole, 0);

        CoInitializeEx(IntPtr.Zero, 0);

        // 2. 启动后台监控工作线程
        Thread workerThread = new Thread(MonitoringWorkerLoop)
        {
            IsBackground = true
        };
        workerThread.Start();

        // 3. 主线程运行 Win32 托盘图标与消息循环
        RunTrayIconMessageLoop();
    }

    /// <summary>
    /// 初始化伪装成“控制面板”的托盘图标并运行消息循环
    /// </summary>
    static void RunTrayIconMessageLoop()
    {
        string className = "ControlPanelTrayMsgWindow";
        _wndProc = WndProc;

        WNDCLASSEX wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf(typeof(WNDCLASSEX)),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            lpszClassName = className
        };
        RegisterClassEx(ref wc);

        IntPtr hWnd = CreateWindowEx(0, className, "ControlPanelProxy", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hWnd == IntPtr.Zero) return;

        // 获取真实的控制面板原生图标
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

        // 添加任务栏托盘图标
        _nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
            hWnd = hWnd,
            uID = 1001,
            uFlags = (int)(NIF_MESSAGE | NIF_ICON | NIF_TIP),
            uCallbackMessage = WM_TRAYICON,
            hIcon = hIcon,
            szTip = "控制面板" // 伪装悬停文本
        };
        Shell_NotifyIcon(NIM_ADD, ref _nid);

        // 标准 Win32 消息循环
        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        // 退出时清理托盘图标
        Shell_NotifyIcon(NIM_DELETE, ref _nid);
    }

    /// <summary>
    /// 窗口过程回调：响应托盘点击事件
    /// </summary>
    static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAYICON)
        {
            uint mouseMsg = (uint)lParam.ToInt64() & 0xFFFF;
            // 左键或右键点击均弹出时间切换菜单
            if (mouseMsg == WM_LBUTTONUP || mouseMsg == WM_RBUTTONUP)
            {
                ShowDurationPopupMenu(hWnd);
            }
            return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 弹出时间切换列表菜单（带选中状态，即选即隐）
    /// </summary>
    static void ShowDurationPopupMenu(IntPtr hWnd)
    {
        IntPtr hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        // 动态构建选项列表：1.0 到 5.0 秒
        for (int i = 0; i < DurationOptions.Length; i++)
        {
            double duration = DurationOptions[i];
            uint flags = MF_STRING;

            // 当前时间显示选中的 ✓
            if (Math.Abs(duration - _minStreamDurationSeconds) < 0.01)
            {
                flags |= MF_CHECKED;
            }
            else
            {
                flags |= MF_UNCHECKED;
            }

            AppendMenu(hMenu, flags, (UIntPtr)(100 + i), $"{duration:0.0} 秒");
        }

        // 退出项 (以备退出程序所需)
        AppendMenu(hMenu, MF_SEPARATOR, UIntPtr.Zero, string.Empty);
        AppendMenu(hMenu, MF_STRING, (UIntPtr)999, "退出");

        GetCursorPos(out POINT pt);
        SetForegroundWindow(hWnd);

        // TPM_RETURNCMD 使得用户选择后直接返回菜单 ID，无需弹窗提示
        int selectedId = TrackPopupMenuEx(hMenu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, hWnd, IntPtr.Zero);
        PostMessage(hWnd, WM_NULL, IntPtr.Zero, IntPtr.Zero); // 规避 Windows 托盘失焦 BUG

        DestroyMenu(hMenu);

        if (selectedId >= 100 && selectedId < 100 + DurationOptions.Length)
        {
            // 实时应用新时间，无任何提示框
            _minStreamDurationSeconds = DurationOptions[selectedId - 100];
            Log($"[设置更新] 判定时间已调整为: {_minStreamDurationSeconds:0.0} 秒");
        }
        else if (selectedId == 999)
        {
            // 正常退出程序
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            DestroyWindow(hWnd);
            PostQuitMessage(0);
            Environment.Exit(0);
        }
    }

    /// <summary>
    /// 后台监听主循环
    /// </summary>
    static void MonitoringWorkerLoop()
    {
        CoInitializeEx(IntPtr.Zero, 0);

        while (true)
        {
            try
            {
                MonitorActiveNetworkStreaming();
                MonitorSpeakerPlayback();
            }
            catch { }

            Thread.Sleep(200); // 200ms 采样
        }
    }

    /// <summary>
    /// 监测向外推流（网络 + 流量 实时动态判定）
    /// </summary>
    static void MonitorActiveNetworkStreaming()
    {
        HashSet<int> networkPids = GetProcessesWithExternalNetwork();
        DateTime now = DateTime.Now;
        var alivePids = new HashSet<int>();

        foreach (int pid in networkPids)
        {
            alivePids.Add(pid);

            IntPtr hProc = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (hProc == IntPtr.Zero) continue;

            try
            {
                string procPath = GetProcessFullPath(hProc, pid);
                string procName = Path.GetFileNameWithoutExtension(procPath);

                // 排除浏览器家族，绝对防止上网误报
                if (ExcludedBrowsers.Contains(procName)) continue;

                if (GetProcessIoCounters(hProc, out IO_COUNTERS io))
                {
                    ulong currentBytes = io.WriteTransferCount;

                    if (!_trackedStreams.TryGetValue(pid, out NetworkStreamTracker tracker))
                    {
                        _trackedStreams[pid] = new NetworkStreamTracker
                        {
                            FirstSurgeTime = now,
                            LastSurgeTime = now,
                            LastBytes = currentBytes,
                            LastSampleTime = now,
                            HasAlerted = false,
                            ProcessFullPath = procPath,
                            ProcessName = procName
                        };
                    }
                    else
                    {
                        double elapsed = (now - tracker.LastSampleTime).TotalSeconds;
                        if (elapsed > 0.15)
                        {
                            double speedKB = ((currentBytes - tracker.LastBytes) / 1024.0) / elapsed;

                            if (speedKB >= TRAFFIC_THRESHOLD_KB)
                            {
                                tracker.LastSurgeTime = now;

                                // 依据动态选定的 _minStreamDurationSeconds 判定
                                if (!tracker.HasAlerted && (now - tracker.FirstSurgeTime).TotalSeconds >= _minStreamDurationSeconds)
                                {
                                    tracker.HasAlerted = true;
                                    Log($"[警报] 检测到画面向外推流传输! 进程: {tracker.ProcessName} (PID: {pid}), 判定时间: {_minStreamDurationSeconds:0.0}秒, 速率: {speedKB:F1} KB/s");
                                    PlayUsbConnect(); // 响铃：插入U盘音
                                }
                            }

                            tracker.LastBytes = currentBytes;
                            tracker.LastSampleTime = now;
                        }
                    }
                }
            }
            finally
            {
                CloseHandle(hProc);
            }
        }

        // 停止/断开检测
        var stoppedPids = new List<int>();
        foreach (var kvp in _trackedStreams)
        {
            int pid = kvp.Key;
            var tracker = kvp.Value;

            bool isProcessDead = !alivePids.Contains(pid);
            bool isTrafficSilent = (now - tracker.LastSurgeTime).TotalSeconds > STOP_TOLERANCE_SECONDS;

            if (isProcessDead || isTrafficSilent)
            {
                if (tracker.HasAlerted)
                {
                    Log($"[恢复] 画面拉流已停止/通道断开! 进程: {tracker.ProcessName} (PID: {pid})");
                    PlayUsbDisconnect(); // 响铃：拔出U盘音
                }
                stoppedPids.Add(pid);
            }
        }

        foreach (int pid in stoppedPids)
        {
            _trackedStreams.Remove(pid);
        }
    }

    /// <summary>
    /// 获取当前系统内所有持有外部 TCP/UDP 连接的进程 PID
    /// </summary>
    static HashSet<int> GetProcessesWithExternalNetwork()
    {
        var pids = new HashSet<int>();

        // 1. TCP 连接表
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2 /* AF_INET */, 5 /* TCP_TABLE_OWNER_PID_ALL */, 0);
        if (size > 0)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, 2, 5, 0) == 0)
                {
                    int numEntries = Marshal.ReadInt32(buffer);
                    IntPtr rowPtr = IntPtr.Add(buffer, 4);
                    for (int i = 0; i < numEntries; i++)
                    {
                        uint state = (uint)Marshal.ReadInt32(rowPtr, 0);
                        uint remoteAddr = (uint)Marshal.ReadInt32(rowPtr, 12);
                        uint pid = (uint)Marshal.ReadInt32(rowPtr, 20);

                        // state == 5 (ESTABLISHED), 排除本机回环
                        if (state == 5 && remoteAddr != 0 && remoteAddr != 0x0100007F)
                        {
                            if (pid > 4) pids.Add((int)pid);
                        }
                        rowPtr = IntPtr.Add(rowPtr, 24);
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        // 2. UDP 传输套接字
        size = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref size, false, 2 /* AF_INET */, 1 /* UDP_TABLE_OWNER_PID */, 0);
        if (size > 0)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedUdpTable(buffer, ref size, false, 2, 1, 0) == 0)
                {
                    int numEntries = Marshal.ReadInt32(buffer);
                    IntPtr rowPtr = IntPtr.Add(buffer, 4);
                    for (int i = 0; i < numEntries; i++)
                    {
                        uint pid = (uint)Marshal.ReadInt32(rowPtr, 8);
                        if (pid > 4) pids.Add((int)pid);
                        rowPtr = IntPtr.Add(rowPtr, 12);
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        return pids;
    }

    /// <summary>
    /// 扬声器外放监听 (远控语音开麦)
    /// </summary>
    static void MonitorSpeakerPlayback()
    {
        IMMDeviceEnumerator enumerator = null;
        IMMDevice speaker = null;
        IAudioSessionManager2 mgr = null;
        IAudioSessionEnumerator sessionEnum = null;
        var currentPids = new HashSet<uint>();

        try
        {
            int hr = CoCreateInstance(CLSID_MMDeviceEnumerator, IntPtr.Zero, 1, IID_IMMDeviceEnumerator, out enumerator);
            if (hr != 0 || enumerator == null) return;

            hr = enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 0, out speaker);
            if (hr != 0 || speaker == null) return;

            Guid iidMgr = IID_IAudioSessionManager2;
            hr = speaker.Activate(ref iidMgr, 23, IntPtr.Zero, out object objMgr);
            if (hr != 0 || objMgr == null) return;

            mgr = (IAudioSessionManager2)objMgr;
            hr = mgr.GetSessionEnumerator(out sessionEnum);
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
                    if (state == 1) // 播放中
                    {
                        session.GetProcessId(out uint pid);
                        if (pid > 0)
                        {
                            currentPids.Add(pid);

                            if (!_activeSpeakerPids.Contains(pid))
                            {
                                string procName = GetProcessNameOnly((int)pid);
                                if (!ExcludedBrowsers.Contains(procName.Replace(".exe", "")))
                                {
                                    Log($"[语音] 远端开麦讲话/向扬声器外放声音: {procName} (PID: {pid})");
                                    _activeSpeakerPids.Add(pid);
                                }
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
            if (mgr != null) Marshal.ReleaseComObject(mgr);
            if (speaker != null) Marshal.ReleaseComObject(speaker);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }

        _activeSpeakerPids.RemoveWhere(pid => !currentPids.Contains(pid));
    }

    static string GetProcessFullPath(IntPtr hProc, int pid)
    {
        var sb = new StringBuilder(1024);
        int size = sb.Capacity;
        if (QueryFullProcessImageName(hProc, 0, sb, ref size)) return sb.ToString();
        return GetProcessNameOnly(pid);
    }

    static string GetProcessNameOnly(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName + ".exe"; }
        catch { return $"PID_{pid}"; }
    }

    static void Log(string msg)
    {
        try
        {
            File.AppendAllText(LogFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
    }
}
