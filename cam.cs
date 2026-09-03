using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

class Program
{
    // ==========================================
    // 1. Win32 音效 API
    // ==========================================
    [DllImport("winmm.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_ALIAS = 0x00010000;

    static void PlayUsbConnect() => PlaySound("DeviceConnect", IntPtr.Zero, SND_ALIAS | SND_ASYNC);
    static void PlayUsbDisconnect() => PlaySound("DeviceDisconnect", IntPtr.Zero, SND_ALIAS | SND_ASYNC);

    // ==========================================
    // 2. Win32 进程全路径查询 API
    // ==========================================
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // ==========================================
    // 3. Windows WASAPI (Core Audio) COM 接口定义
    // ==========================================
    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

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
    // 4. 业务逻辑
    // ==========================================
    private static readonly string LogFilePath = @"D:\Microphone_Usage_Log.txt";
    private static bool _wasCameraInUse = false;
    private static readonly HashSet<uint> _activeMicPids = new HashSet<uint>();

    static void Main(string[] args)
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("摄像头/麦克风 硬件级低延迟监听服务 (bflat Native)");
        Console.WriteLine("麦克风引擎: WASAPI CoreAudio 会话级轮询 (精确PID/零延迟)");
        Console.WriteLine("摄像头引擎: 全局注册表 + 进程存活交叉校验 (防幽灵误报)");
        Console.WriteLine("==================================================");

        while (true)
        {
            try
            {
                CheckCameraState();
                CheckMicrophoneViaWASAPI();
            }
            catch (Exception ex)
            {
                // 生产环境静默或记录内部异常
                Debug.WriteLine($"Error: {ex.Message}");
            }

            Thread.Sleep(150); // 150ms 高频低开销轮询，响应极快
        }
    }

    /// <summary>
    /// 利用 WASAPI 直接向声卡驱动查询麦克风占用
    /// </summary>
    static void CheckMicrophoneViaWASAPI()
    {
        IMMDeviceEnumerator enumerator = null;
        IMMDevice micDevice = null;
        IAudioSessionManager2 sessionManager = null;
        IAudioSessionEnumerator sessionEnum = null;

        var currentPids = new HashSet<uint>();

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            // eCapture = 1, eConsole = 0
            int hr = enumerator.GetDefaultAudioEndpoint(1, 0, out micDevice);
            if (hr != 0 || micDevice == null) return;

            Guid IID_IAudioSessionManager2 = typeof(IAudioSessionManager2).GUID;
            hr = micDevice.Activate(ref IID_IAudioSessionManager2, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object sessionManagerObj);
            if (hr != 0 || sessionManagerObj == null) return;

            sessionManager = (IAudioSessionManager2)sessionManagerObj;
            hr = sessionManager.GetSessionEnumerator(out sessionEnum);
            if (hr != 0 || sessionEnum == null) return;

            sessionEnum.GetCount(out int count);

            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl2 sessionControl = null;
                try
                {
                    sessionEnum.GetSession(i, out sessionControl);
                    if (sessionControl == null) continue;

                    sessionControl.GetState(out int state);
                    // AudioSessionStateActive = 1 (声卡正在从该会话捕获数据)
                    if (state == 1)
                    {
                        sessionControl.GetProcessId(out uint pid);
                        if (pid > 0)
                        {
                            currentPids.Add(pid);

                            if (!_activeMicPids.Contains(pid))
                            {
                                string procName = GetProcessNameAndPath((int)pid);
                                string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                                string logContent = $"[时间: {timeStr}] [PID: {pid}] 进程调用了麦克风: {procName}{Environment.NewLine}";

                                Console.Write(logContent);
                                WriteLog(logContent);

                                _activeMicPids.Add(pid);
                            }
                        }
                    }
                }
                finally
                {
                    if (sessionControl != null) Marshal.ReleaseComObject(sessionControl);
                }
            }
        }
        finally
        {
            if (sessionEnum != null) Marshal.ReleaseComObject(sessionEnum);
            if (sessionManager != null) Marshal.ReleaseComObject(sessionManager);
            if (micDevice != null) Marshal.ReleaseComObject(micDevice);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }

        // 移除已经停止录音的 PID
        _activeMicPids.RemoveWhere(pid => !currentPids.Contains(pid));
    }

    /// <summary>
    /// 检测摄像头：穿透 HKCU 与 HKLM，并交叉验证进程存活状态
    /// </summary>
    static void CheckCameraState()
    {
        bool isInUse = IsCameraPhysicallyActive();

        if (isInUse && !_wasCameraInUse)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 摄像头激活 -> 播放连接音效");
            PlayUsbConnect();
            _wasCameraInUse = true;
        }
        else if (!isInUse && _wasCameraInUse)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 摄像头关闭 -> 播放断开音效");
            PlayUsbDisconnect();
            _wasCameraInUse = false;
        }
    }

    static bool IsCameraPhysicallyActive()
    {
        string subPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
        
        // 同时扫描 HKCU (用户级) 和 HKLM (系统级/服务级)
        return CheckConsentStoreKey(Registry.CurrentUser, subPath) || 
               CheckConsentStoreKey(Registry.LocalMachine, subPath);
    }

    static bool CheckConsentStoreKey(RegistryKey rootKey, string subPath)
    {
        using (var baseKey = rootKey.OpenSubKey(subPath))
        {
            if (baseKey == null) return false;

            // 1. 检查桌面程序 (NonPackaged)
            using (var nonPackagedKey = baseKey.OpenSubKey("NonPackaged"))
            {
                if (nonPackagedKey != null)
                {
                    foreach (var keyName in nonPackagedKey.GetSubKeyNames())
                    {
                        using (var appKey = nonPackagedKey.OpenSubKey(keyName))
                        {
                            if (appKey != null && CheckIsActiveWithLiveness(appKey, keyName))
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            // 2. 检查 UWP 应用
            foreach (var keyName in baseKey.GetSubKeyNames())
            {
                if (keyName.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase)) continue;

                using (var appKey = baseKey.OpenSubKey(keyName))
                {
                    if (appKey != null && CheckIsActiveUwp(appKey, keyName))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 检查并进行存活校验：解决由于程序崩溃导致的残留 LastUsedTimeStop == 0 误报
    /// </summary>
    static bool CheckIsActiveWithLiveness(RegistryKey appKey, string rawKeyName)
    {
        var stopTimeObj = appKey.GetValue("LastUsedTimeStop");
        var startTimeObj = appKey.GetValue("LastUsedTimeStart");

        if (startTimeObj is long startTime && startTime > 0)
        {
            long stopTime = stopTimeObj is long l ? l : -1;

            // 注册表显示正在使用 (stopTime == 0 或 停止时间早于启动时间)
            if (stopTime == 0 || stopTime < startTime)
            {
                // 还原 exe 真实进程名称
                string appPath = rawKeyName.Replace('#', '\\');
                string exeName = Path.GetFileNameWithoutExtension(appPath);

                if (string.IsNullOrEmpty(exeName)) return false;

                // 核心防误报：去系统检查这个进程是否真的还活在内存中
                Process[] procs = Process.GetProcessesByName(exeName);
                if (procs.Length > 0)
                {
                    // 进程确实活着，确实正在占用
                    return true;
                }
                // 如果进程都退出了，说明是注册表脏数据，忽略
            }
        }
        return false;
    }

    static bool CheckIsActiveUwp(RegistryKey appKey, string packageFamilyName)
    {
        var stopTimeObj = appKey.GetValue("LastUsedTimeStop");
        var startTimeObj = appKey.GetValue("LastUsedTimeStart");

        if (startTimeObj is long startTime && startTime > 0)
        {
            long stopTime = stopTimeObj is long l ? l : -1;
            if (stopTime == 0 || stopTime < startTime)
            {
                // UWP 存活检测：检查常见 UWP 宿主是否存活
                return Process.GetProcessesByName("ApplicationFrameHost").Length > 0 ||
                       Process.GetProcessesByName("WindowsCamera").Length > 0;
            }
        }
        return false;
    }

    /// <summary>
    /// 跨权限/全用户 获取真实进程全路径
    /// </summary>
    static string GetProcessNameAndPath(int pid)
    {
        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(hProcess, 0, sb, ref size))
                {
                    return sb.ToString();
                }
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        try
        {
            var p = Process.GetProcessById(pid);
            return p.ProcessName + ".exe";
        }
        catch
        {
            return $"PID_{pid} (无法获取路径/已退出)";
        }
    }

    static void WriteLog(string content)
    {
        try
        {
            File.AppendAllText(LogFilePath, content, Encoding.UTF8);
        }
        catch { }
    }
}
