using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LimbusWindowResizeFix
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public sealed class Plugin : BasePlugin
    {
        public const string GUID = "com.you.limbuswindowresizefix";
        public const string NAME = "LimbusWindowResizeFix";
        public const string VERSION = "1.0.0";

        private static ManualLogSource? logger;
        private static Timer? timer;
        private static int ticks;
        private static readonly HashSet<IntPtr> patchedWindows = new();

        public override void Load()
        {
            InitializeLogger(base.Log);
            logger!.LogInfo($"{NAME} {VERSION} loading...");

            StartTimer();

            logger!.LogInfo($"{NAME} ready.");
        }

        public override bool Unload()
        {
            StopTimer();
            return true;
        }

        private static void InitializeLogger(ManualLogSource source)
        {
            logger = source;
        }

        private static void StartTimer()
        {
            ApplyResizeStyles();
            timer = new Timer(_ => ApplyResizeStyles(), null, 250, 1000);
        }

        private static void StopTimer()
        {
            timer?.Dispose();
            timer = null;
        }

        private static void ApplyResizeStyles()
        {
            try
            {
                ticks++;
                var windows = WindowTools.FindProcessWindows();
                var patched = 0;

                foreach (var window in windows)
                {
                    if (WindowTools.EnableResize(window, out var before, out var after))
                    {
                        patched++;
                        if (patchedWindows.Add(window))
                        {
                            logger?.LogInfo(
                                $"Enabled resizing for HWND 0x{window.ToInt64():X} style 0x{before:X} -> 0x{after:X}.");
                        }
                    }
                }

                if (ticks is 1 or 10 or 30 && windows.Count == 0)
                    logger?.LogWarning("No Limbus window handle found yet; continuing to poll.");

                if (ticks % 30 == 0 && patchedWindows.Count > 0)
                    logger?.LogInfo($"Window resize fix active; patched {patchedWindows.Count} window handle(s) so far.");
            }
            catch (Exception ex)
            {
                logger?.LogError($"Window resize fix failed: {ex}");
            }
        }
    }

    internal static class WindowTools
    {
        private const int GWL_STYLE = -16;

        private const long WS_CAPTION = 0x00C00000L;
        private const long WS_SYSMENU = 0x00080000L;
        private const long WS_THICKFRAME = 0x00040000L;
        private const long WS_MINIMIZEBOX = 0x00020000L;
        private const long WS_MAXIMIZEBOX = 0x00010000L;
        private const long RequiredStyle = WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;

        public static List<IntPtr> FindProcessWindows()
        {
            var pid = (uint)Environment.ProcessId;
            var result = new List<IntPtr>();
            var seen = new HashSet<IntPtr>();

            EnumWindows((hWnd, _) =>
            {
                AddIfGameWindow(hWnd, pid, result, seen);
                return true;
            }, IntPtr.Zero);

            foreach (ProcessThread thread in Process.GetCurrentProcess().Threads)
            {
                EnumThreadWindows((uint)thread.Id, (hWnd, _) =>
                {
                    AddIfGameWindow(hWnd, pid, result, seen);
                    return true;
                }, IntPtr.Zero);
            }

            AddIfGameWindow(GetActiveWindow(), pid, result, seen);
            AddIfGameWindow(Process.GetCurrentProcess().MainWindowHandle, pid, result, seen);

            return result;
        }

        public static bool EnableResize(IntPtr hWnd, out long before, out long after)
        {
            before = 0;
            after = 0;
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
                return false;

            before = GetWindowLongPtr(hWnd, GWL_STYLE).ToInt64();
            after = before | RequiredStyle;
            if (after == before)
                return false;

            SetWindowLongPtr(hWnd, GWL_STYLE, new IntPtr(after));
            SetWindowPos(
                hWnd,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

            return true;
        }

        private static void AddIfGameWindow(IntPtr hWnd, uint pid, List<IntPtr> result, HashSet<IntPtr> seen)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd) || !seen.Add(hWnd))
                return;

            GetWindowThreadProcessId(hWnd, out var windowPid);
            if (windowPid != pid)
                return;

            GetWindowRect(hWnd, out var rect);
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < 320 || height < 200)
                return;

            result.Add(hWnd);
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumThreadWindows(uint dwThreadId, EnumWindowsProc lpfn, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}
