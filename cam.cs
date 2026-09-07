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
    // 核心监控参数
    // =========================================================================
    private const double MIN_STREAM_DURATION_SECONDS = 1.0; // 持续推流满 1 秒即响
    private const double TRAFFIC_THRESHOLD_KB = 80.0;       // 视频流速率判定门限 (KB/s)
    private const double STOP_TOLERANCE_SECONDS = 1.5;      // 停止超过 1.5 秒视为断开
    private static readonly string LogFilePath = @"D:\Remote_Activity_Log.txt";

    // 浏览器隔离名单（彻底绝杜上网查资料误报）
    private static readonly HashSet<string> ExcludedBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "opera", "brave", "360se", "360chrome",
        "qqbrowser", "sogouexplorer", "2345explorer", "liebao", "maxthon",
        "msedgewebview2", "conhost"
    };

    // =========================================================================
    // Win32 音效 & 窗口
    // =========================================================================
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

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // =========================================================================
    // Windows 内核级网络套接字 (iphlpapi.dll)
    // =========================================================================
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int TableClass, uint Reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable, ref int pdwSize, bool bOrder, int ulAf, int TableClass, uint Reserved);

    // =========================================================================
    // 进程 I/O 查询
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    // =========================================================================
    // WASAPI COM (扬声器外放/远端说话监控)
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
    // 状态管理容器
    // =========================================================================
    private class NetworkStreamTracker
    {
        public DateTime FirstSurgeTime;   // 首次检测到网络突发的时间
        public DateTime LastSurgeTime;    // 最近一次高速推流时间
        public ulong LastBytes;           // 上次字节数
        public DateTime LastSampleTime;   // 上次采样时间
        public bool HasAlerted;           // 是否已发出插U盘提示音
        public string ProcessFullPath;    // 进程全路径
        public string ProcessName;        // 进程名
    }

    private static readonly Dictionary<int, NetworkStreamTracker> _trackedStreams = new();
    private static readonly HashSet<uint> _activeSpeakerPids = new();

    static void Main()
    {
        IntPtr hWnd = GetConsoleWindow();
        if (hWnd != IntPtr.Zero) ShowWindow(hWnd, 0);

        CoInitializeEx(IntPtr.Zero, 0);

        while (true)
        {
            try
            {
                // 1. 核心：网络+流量 强制交叉校验（主进程/附属进程全覆盖）
                MonitorActiveNetworkStreaming();

                // 2. 扬声器外放语音监听
                MonitorSpeakerPlayback();
            }
            catch { }

            Thread.Sleep(200); // 200ms 高频低功耗采样
        }
    }

    /// <summary>
    /// 获取当前系统内所有持有“外部网络通信（TCP已建立/UDP传输）”的进程 PID
    /// </summary>
    static HashSet<int> GetProcessesWithExternalNetwork()
    {
        var pids = new HashSet<int>();

        // 1. 获取 TCP 连接表 (仅筛选对外已建立连接 ESTABLISHED)
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

                        // state == 5 (MIB_TCP_STATE_ESTAB)，排除本机回环 127.0.0.1 和 0.0.0.0
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

        // 2. 获取 UDP 传输套接字 (许多推流采用 WebRTC/UDP)
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
    /// 核心检测引擎：只有“外连网络” + “持续高速发包”才触发
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

                            // 既连着外网，每秒又发往外网超过 80KB（典型视频流）
                            if (speedKB >= TRAFFIC_THRESHOLD_KB)
                            {
                                tracker.LastSurgeTime = now;

                                // 持续满 1.0 秒，立刻报警
                                if (!tracker.HasAlerted && (now - tracker.FirstSurgeTime).TotalSeconds >= MIN_STREAM_DURATION_SECONDS)
                                {
                                    tracker.HasAlerted = true;
                                    Log($"[警报] 检测到画面向外推流传输! 进程: {tracker.ProcessName} (PID: {pid}), 路径: {tracker.ProcessFullPath}, 瞬时速率: {speedKB:F1} KB/s");
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

        // =========================================================================
        // 停止/退出 检测：附属程序被杀 或 主进程断开推流
        // =========================================================================
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
                    Log($"[恢复] 画面拉流已停止/传输通道已关闭! 进程: {tracker.ProcessName} (PID: {pid})");
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
    /// 扬声器外放语音监听 (对方在远端说话)
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

            hr = enumerator.GetDefaultAudioEndpoint(0 /* eRender: 扬声器 */, 0, out speaker);
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
                    if (state == 1) // 扬声器正在发声
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
                                    Log($"[语音] 远端开麦讲话/向扬声器输出声音: {procName} (PID: {pid})");
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
