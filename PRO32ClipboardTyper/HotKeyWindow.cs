using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PRO32ClipboardTyper;

internal sealed class HotKeyWindow : NativeWindow, IDisposable
{
    private const int PasteHotKeyId = 0x5032;
    private const int StopHotKeyId = 0x5033;

    private bool _stopHotKeyRegistered;

    public event EventHandler? PasteHotKeyPressed;
    public event EventHandler? StopHotKeyPressed;

    public HotKeyWindow()
    {
        CreateHandle(new CreateParams());

        bool registered = NativeMethods.RegisterHotKey(
            Handle,
            PasteHotKeyId,
            NativeMethods.MOD_CONTROL |
            NativeMethods.MOD_SHIFT |
            NativeMethods.MOD_NOREPEAT,
            (uint)Keys.V);

        if (!registered)
        {
            int error = Marshal.GetLastWin32Error();
            DestroyHandle();

            throw new Win32Exception(
                error,
                "Не удалось зарегистрировать глобальную комбинацию Ctrl+Shift+V. " +
                "Вероятно, она уже занята другой программой.");
        }
    }

    public void EnableStopHotKey()
    {
        if (_stopHotKeyRegistered)
            return;

        bool registered = NativeMethods.RegisterHotKey(
            Handle,
            StopHotKeyId,
            NativeMethods.MOD_CONTROL |
            NativeMethods.MOD_SHIFT |
            NativeMethods.MOD_NOREPEAT,
            (uint)Keys.C);

        if (!registered)
        {
            int error = Marshal.GetLastWin32Error();

            throw new Win32Exception(
                error,
                "Не удалось зарегистрировать Ctrl+Shift+C для остановки ввода. " +
                "Остановить ввод всё равно можно через меню иконки в трее.");
        }

        _stopHotKeyRegistered = true;
    }

    public void DisableStopHotKey()
    {
        if (!_stopHotKeyRegistered)
            return;

        NativeMethods.UnregisterHotKey(Handle, StopHotKeyId);
        _stopHotKeyRegistered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY)
        {
            int hotKeyId = m.WParam.ToInt32();

            if (hotKeyId == PasteHotKeyId)
            {
                PasteHotKeyPressed?.Invoke(this, EventArgs.Empty);
            }
            else if (hotKeyId == StopHotKeyId)
            {
                StopHotKeyPressed?.Invoke(this, EventArgs.Empty);
            }
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            DisableStopHotKey();
            NativeMethods.UnregisterHotKey(Handle, PasteHotKeyId);
            DestroyHandle();
        }

        GC.SuppressFinalize(this);
    }
}
