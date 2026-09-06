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
    // 2. 进程 I/O 速率监测
    // ==========================================
    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount; // 包含网络 Socket 发送量
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
    // 3. WASAPI 音频输出监听 (对方远程说话外放)
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
    // 4. 业务状态跟踪
    // ==========================================
    private static readonly string LogFilePath = @"D:\Remote_Activity_Log.txt";

    // 判定为视频流的速率阈值 (KB/s)
    private const double VIDEO_STREAM_THRESHOLD_KB = 120.0;
    // 必须持续达到该时长才触发提示音 (秒)
    private const double MIN_STREAM_DURATION_SECONDS = 5.0;
    // 流量中断超过该时长判定为彻底停止 (秒)
    private const double STOP_TOLERANCE_SECONDS = 2.0;

    // 记录历史 IO
    private static readonly Dictionary<int, (ulong LastBytes, DateTime LastTime)> _processIoHistory = new();

    // 持续传输计时器状态类
    private class StreamState
    {
        public DateTime StartTime;         // 流量首次超标的时间
        public DateTime LastHighTraffic;   // 最近一次超标的时间
        public bool HasAlerted;            // 是否已经播放过“插入”提示音
    }

    private static readonly Dictionary<int, StreamState> _streamStates = new();
    private static readonly HashSet<uint> _activeSpeakerPids = new();

    static void Main(string[] args)
    {
        // 自动隐藏黑框静默运行
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
                // 1. 持续5秒以上视频推流监控
                MonitorDataStreamingWithTimer();

                // 2. 远控声音外放监控
                MonitorRemoteVoicePlayback();
            }
            catch { }

            Thread.Sleep(300); // 300ms 采样周期
        }
    }

    /// <summary>
    /// 带 5 秒防误报计时器的流量检测
    /// </summary>
    static void MonitorDataStreamingWithTimer()
    {
        Process[] processes = Process.GetProcesses();
        DateTime now = DateTime.Now;
        var activePidsInSystem = new HashSet<int>();

        foreach (var p in processes)
        {
            int pid = p.Id;
            if (pid <= 4) continue;
            activePidsInSystem.Add(pid);

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
                                    // 首次进入高速传输，开启计时
                                    _streamStates[pid] = new StreamState
                                    {
                                        StartTime = now,
                                        LastHighTraffic = now,
                                        HasAlerted = false
                                    };
                                }
                                else
                                {
                                    // 持续高速传输，更新最后活跃时间
                                    state.LastHighTraffic = now;

                                    // 核心逻辑：只有持续超标 >= 5 秒且尚未报警，才响铃！
                                    if (!state.HasAlerted && (now - state.StartTime).TotalSeconds >= MIN_STREAM_DURATION_SECONDS)
                                    {
                                        state.HasAlerted = true;
                                        string procPath = GetProcessPath(hProcess, pid);
                                        WriteLog($"[时间: {now:yyyy-MM-dd HH:mm:ss}] [确认拉流] 进程连续传输超过 5 秒: {procPath}, 瞬时速率: {speedKBps:F1} KB/s");
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

            // 进程死掉或者停止传输超过 2 秒
            if (isProcessDead || isTrafficStopped)
            {
                // 如果曾经响过“插入”提示音，现在停止了，必须响“拔出”提示音
                if (state.HasAlerted)
                {
                    WriteLog($"[时间: {now:yyyy-MM-dd HH:mm:ss}] [停止拉流] PID: {pid} 已停止持续推流");
                    PlayUsbDisconnect(); // 触发：拔出U盘音效
                }

                // 如果没达到 5 秒就停了（纯心跳探测包），直接丢弃，不响任何音效，完美防误报
                toRemove.Add(pid);
            }
        }

        foreach (int pid in toRemove)
        {
            _streamStates.Remove(pid);
        }
    }

    /// <summary>
    /// 扬声器输出检测（对方远端开麦讲话）
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

            // eRender = 0 (输出/播放设备)
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
                    if (state == 1) // 正在播放声音
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
