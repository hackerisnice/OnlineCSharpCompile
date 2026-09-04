using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;

class Program
{
    // ==========================================
    // 1. 系统音效 & 窗口隐藏
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

    // ==========================================
    // 2. 进程 I/O 速率监测 (判断是否在传输视频数据)
    // ==========================================
    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount; // 包含了网络 Socket 发送和磁盘写入的总字节数
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

    // ==========================================
    // 3. WASAPI 音频输出监听 (监听对方远程发声)
    // ==========================================
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IMMDeviceEnumerator ppv);

    private static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate([In] ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr AudioSessionGuid, int StreamFlags, out IntPtr SessionControl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr AudioSessionGuid, int StreamFlags, out IntPtr AudioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
    }

    [ComImport]
    [Guid("E2F56580-1476-4EC6-A0DC-05D77DE16D77")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int SessionCount);
        [PreserveSig] int GetSession(int SessionIndex, out IAudioSessionControl2 Session);
    }

    [ComImport]
    [Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c23")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
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

    // ==========================================
    // 4. 业务监控状态
    // ==========================================
    private static readonly string LogFilePath = @"D:\Remote_Activity_Log.txt";
    
    // 判定为“正在视频推流”的写入速率门限（单位：KB/s）
    // 视频传输（720P/1080P）通常在 150KB/s ~ 1500KB/s 以上
    private const double VIDEO_STREAM_THRESHOLD_KB = 120.0;

    // 记录各进程历史 IO 状态
    private static readonly Dictionary<int, (ulong LastBytes, DateTime LastTime)> _processIoHistory = new();
    private static readonly HashSet<int> _activeStreamingPids = new();
    private static readonly HashSet<uint> _activeSpeakerPids = new();

    static void Main(string[] args)
    {
        // 自动隐藏黑框（静默运行）
        IntPtr hConsole = GetConsoleWindow();
        if (hConsole != IntPtr.Zero)
        {
            ShowWindow(hConsole, SW_HIDE);
        }

        CoInitializeEx(IntPtr.Zero, 0);

        while (true)
        {
            try
            {
                // 1. 监控：远控推流传输（突发大流量发送 -> 拔插U盘音效）
                MonitorDataStreaming();

                // 2. 监控：远控说话外放（扬声器播放声音 -> 记录日志到 D 盘）
                MonitorRemoteVoicePlayback();
            }
            catch { }

            Thread.Sleep(300); // 300ms 采样周期，灵敏且极低 CPU 占用
        }
    }

    /// <summary>
    /// 监测进程的实时写入速率，判断是否正在向远端发送视频流
    /// </summary>
    static void MonitorDataStreaming()
    {
        Process[] processes = Process.GetProcesses();
        DateTime now = DateTime.Now;
        var currentActiveStreaming = new HashSet<int>();

        foreach (var p in processes)
        {
            int pid = p.Id;
            if (pid <= 4) continue; // 跳过 System/Idle 进程

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
                            // 计算此进程当前的写入速率 (KB/s)
                            double speedKBps = ((currentBytes - lastRecord.LastBytes) / 1024.0) / elapsedSeconds;

                            // 如果速率超过阈值，判定为正在传输推流数据
                            if (speedKBps >= VIDEO_STREAM_THRESHOLD_KB)
                            {
                                currentActiveStreaming.Add(pid);

                                if (!_activeStreamingPids.Contains(pid))
                                {
                                    string procPath = GetProcessPath(hProcess, pid);
                                    WriteLog($"[时间: {now:yyyy-MM-dd HH:mm:ss}] [开始推流] 进程: {procPath}, 速率: {speedKBps:F1} KB/s");
                                    PlayUsbConnect(); // 开始传输画面 -> 发出插 U 盘提示音
                                    _activeStreamingPids.Add(pid);
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

        // 检查哪些进程停止了推流
        var stoppedPids = new List<int>();
        foreach (int pid in _activeStreamingPids)
        {
            if (!currentActiveStreaming.Contains(pid))
            {
                stoppedPids.Add(pid);
                WriteLog($"[时间: {now:yyyy-MM-dd HH:mm:ss}] [停止推流] PID: {pid} 已断开/停止视频传输");
                PlayUsbDisconnect(); // 停止传输画面 -> 发出拔 U 盘提示音
            }
        }

        foreach (int pid in stoppedPids)
        {
            _activeStreamingPids.Remove(pid);
        }
    }

    /// <summary>
    /// 监测扬声器/耳机输出：当有远控进程在本地播放对方语音时记录日志
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

            // eRender = 0 (监听扬声器/播放输出端), eConsole = 0
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
                    // state == 1 代表该会话正在外放声音
                    if (state == 1)
                    {
                        session.GetProcessId(out uint pid);
                        if (pid > 0)
                        {
                            currentPids.Add(pid);

                            if (!_activeSpeakerPids.Contains(pid))
                            {
                                string procName = GetProcessNameOnly((int)pid);
                                // 过滤掉常见的正常播放软件（如果需要可以自行调整）
                                WriteLog($"[时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}] [语音上线] 进程正在外放声音 (对方可能在讲话): {procName} (PID: {pid})");
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
