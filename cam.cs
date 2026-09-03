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
    // 1. 隐藏控制台黑框 (代码层实现，免去复杂的链接器配置)
    // ==========================================
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_HIDE = 0;

    // ==========================================
    // 2. Win32 音效 API
    // ==========================================
    [DllImport("winmm.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_ALIAS = 0x00010000;

    static void PlayUsbConnect() => PlaySound("DeviceConnect", IntPtr.Zero, SND_ALIAS | SND_ASYNC);
    static void PlayUsbDisconnect() => PlaySound("DeviceDisconnect", IntPtr.Zero, SND_ALIAS | SND_ASYNC);

    // ==========================================
    // 3. Win32 进程全路径查询 API
    // ==========================================
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // ==========================================
    // 4. 原生 COM 激活 API (Native AOT 专用)
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
    // 5. 业务逻辑
    // ==========================================
    private static readonly string LogFilePath = @"D:\Microphone_Usage_Log.txt";
    private static bool _wasCameraInUse = false;
    private static readonly HashSet<uint> _activeMicPids = new HashSet<uint>();

    static void Main(string[] args)
    {
        // 1. 静默运行：自动隐藏控制台黑框 (调试时可以把这行注释掉)
        IntPtr hConsole = GetConsoleWindow();
        if (hConsole != IntPtr.Zero)
        {
            ShowWindow(hConsole, SW_HIDE);
        }

        // 2. 初始化 COM 线程环境 (COINIT_MULTITHREADED = 0)
        CoInitializeEx(IntPtr.Zero, 0);

        while (true)
        {
            try
            {
                CheckCameraState();
                CheckMicrophoneViaWASAPI();
            }
            catch { }

            Thread.Sleep(150); // 150毫秒高频低功耗轮询
        }
    }

    static void CheckMicrophoneViaWASAPI()
    {
        IMMDeviceEnumerator enumerator = null;
        IMMDevice micDevice = null;
        IAudioSessionManager2 sessionManager = null;
        IAudioSessionEnumerator sessionEnum = null;

        var currentPids = new HashSet<uint>();

        try
        {
            // 通过 Win32 原生 CoCreateInstance 实例化
            int hr = CoCreateInstance(CLSID_MMDeviceEnumerator, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, IID_IMMDeviceEnumerator, out enumerator);
            if (hr != 0 || enumerator == null) return;

            // eCapture = 1, eConsole = 0
            hr = enumerator.GetDefaultAudioEndpoint(1, 0, out micDevice);
            if (hr != 0 || micDevice == null) return;

            Guid iidMgr = IID_IAudioSessionManager2;
            hr = micDevice.Activate(ref iidMgr, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object sessionManagerObj);
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
                    // 状态 1 = AudioSessionStateActive
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

        _activeMicPids.RemoveWhere(pid => !currentPids.Contains(pid));
    }

    static void CheckCameraState()
    {
        bool isInUse = IsCameraPhysicallyActive();

        if (isInUse && !_wasCameraInUse)
        {
            PlayUsbConnect();
            _wasCameraInUse = true;
        }
        else if (!isInUse && _wasCameraInUse)
        {
            PlayUsbDisconnect();
            _wasCameraInUse = false;
        }
    }

    static bool IsCameraPhysicallyActive()
    {
        string subPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
        return CheckConsentStoreKey(Registry.CurrentUser, subPath) || 
               CheckConsentStoreKey(Registry.LocalMachine, subPath);
    }

    static bool CheckConsentStoreKey(RegistryKey rootKey, string subPath)
    {
        using (var baseKey = rootKey.OpenSubKey(subPath))
        {
            if (baseKey == null) return false;

            // 1. 桌面程序
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

            // 2. UWP 应用
            foreach (var keyName in baseKey.GetSubKeyNames())
            {
                if (keyName.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase)) continue;

                using (var appKey = baseKey.OpenSubKey(keyName))
                {
                    if (appKey != null && CheckIsActiveUwp(appKey))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    static bool CheckIsActiveWithLiveness(RegistryKey appKey, string rawKeyName)
    {
        var stopTimeObj = appKey.GetValue("LastUsedTimeStop");
        var startTimeObj = appKey.GetValue("LastUsedTimeStart");

        if (startTimeObj is long startTime && startTime > 0)
        {
            long stopTime = stopTimeObj is long l ? l : -1;
            if (stopTime == 0 || stopTime < startTime)
            {
                string appPath = rawKeyName.Replace('#', '\\');
                string exeName = Path.GetFileNameWithoutExtension(appPath);

                if (string.IsNullOrEmpty(exeName)) return false;

                // 交叉校验：进程必须真正存活才判定为占用
                return Process.GetProcessesByName(exeName).Length > 0;
            }
        }
        return false;
    }

    static bool CheckIsActiveUwp(RegistryKey appKey)
    {
        var stopTimeObj = appKey.GetValue("LastUsedTimeStop");
        var startTimeObj = appKey.GetValue("LastUsedTimeStart");

        if (startTimeObj is long startTime && startTime > 0)
        {
            long stopTime = stopTimeObj is long l ? l : -1;
            if (stopTime == 0 || stopTime < startTime)
            {
                return Process.GetProcessesByName("ApplicationFrameHost").Length > 0 ||
                       Process.GetProcessesByName("WindowsCamera").Length > 0;
            }
        }
        return false;
    }

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
            File.AppendAllText(LogFilePath, content, Encoding.UTF8);
        }
        catch { }
    }
}
