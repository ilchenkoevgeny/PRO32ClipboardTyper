using System.Diagnostics;
using System.Text;

namespace PRO32ClipboardTyper;

internal static class PRO32WindowService
{
    private const string TargetWindowClass = "Chrome_WidgetWin_1";
    private const string DashboardTitlePart = "PRO32 Connect Dashboard";
    private const string DashboardProcessName = "PRO32 Connect Dashboard";

    public static IntPtr FindSessionWindow()
    {
        IntPtr foregroundWindow = NativeMethods.GetForegroundWindow();

        if (IsPro32SessionWindow(foregroundWindow))
            return foregroundWindow;

        IntPtr result = IntPtr.Zero;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!IsPro32SessionWindow(hWnd))
                return true;

            result = hWnd;
            return false;
        }, IntPtr.Zero);

        return result;
    }

    public static bool ActivateWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
            return false;

        if (NativeMethods.IsIconic(hWnd))
            NativeMethods.ShowWindowAsync(hWnd, NativeMethods.SW_RESTORE);

        IntPtr foregroundWindow = NativeMethods.GetForegroundWindow();

        if (foregroundWindow == hWnd)
            return true;

        uint currentThreadId = NativeMethods.GetCurrentThreadId();
        uint foregroundThreadId = foregroundWindow != IntPtr.Zero
            ? NativeMethods.GetWindowThreadProcessId(foregroundWindow, out _)
            : 0;
        uint targetThreadId = NativeMethods.GetWindowThreadProcessId(hWnd, out _);

        bool attachedForeground = false;
        bool attachedTarget = false;

        try
        {
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
            {
                attachedForeground = NativeMethods.AttachThreadInput(
                    currentThreadId,
                    foregroundThreadId,
                    true);
            }

            if (targetThreadId != 0 && targetThreadId != currentThreadId)
            {
                attachedTarget = NativeMethods.AttachThreadInput(
                    currentThreadId,
                    targetThreadId,
                    true);
            }

            NativeMethods.BringWindowToTop(hWnd);
            NativeMethods.SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attachedTarget)
            {
                NativeMethods.AttachThreadInput(
                    currentThreadId,
                    targetThreadId,
                    false);
            }

            if (attachedForeground)
            {
                NativeMethods.AttachThreadInput(
                    currentThreadId,
                    foregroundThreadId,
                    false);
            }
        }

        // SetForegroundWindow может завершиться до фактической смены foreground.
        for (int i = 0; i < 30; i++)
        {
            if (NativeMethods.GetForegroundWindow() == hWnd)
                return true;

            Thread.Sleep(10);
        }

        return NativeMethods.GetForegroundWindow() == hWnd;
    }

    public static string GetWindowTitle(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
            return string.Empty;

        int length = NativeMethods.GetWindowTextLength(hWnd);

        if (length <= 0)
            return string.Empty;

        StringBuilder builder = new(length + 1);
        NativeMethods.GetWindowText(hWnd, builder, builder.Capacity);

        return builder.ToString();
    }

    private static bool IsPro32SessionWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hWnd))
            return false;

        string className = GetWindowClass(hWnd);

        if (!className.Equals(TargetWindowClass, StringComparison.OrdinalIgnoreCase))
            return false;

        // Не привязываемся к заголовку удалённой сессии. В разных версиях PRO32
        // он может содержать имя компьютера/пользователя и вообще не иметь текста
        // "PRO32 Connect". Надёжнее проверить, что HWND принадлежит процессу PRO32.
        if (!IsPro32ProcessWindow(hWnd))
            return false;

        string title = GetWindowTitle(hWnd);

        if (string.IsNullOrWhiteSpace(title))
            return false;

        // Dashboard тоже является Electron/Chromium-окном того же процесса.
        // Нам нужна именно удалённая сессия.
        if (title.Contains(DashboardTitlePart, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static bool IsPro32ProcessWindow(IntPtr hWnd)
    {
        _ = NativeMethods.GetWindowThreadProcessId(hWnd, out uint processId);

        if (processId == 0)
            return false;

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName.Equals(
                DashboardProcessName,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            // Процесс успел завершиться между EnumWindows и проверкой PID.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string GetWindowClass(IntPtr hWnd)
    {
        StringBuilder builder = new(256);
        NativeMethods.GetClassName(hWnd, builder, builder.Capacity);
        return builder.ToString();
    }
}
