using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PRO32ClipboardTyper;

internal static class ClipboardService
{
    private const int ReadAttempts = 80;
    private const int RetryDelayMs = 25;

    public static string GetText()
    {
        int lastError = 0;

        for (int attempt = 0; attempt < ReadAttempts; attempt++)
        {
            uint sequenceBefore = NativeMethods.GetClipboardSequenceNumber();

            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                lastError = Marshal.GetLastWin32Error();
                Thread.Sleep(RetryDelayMs);
                continue;
            }

            string? text = null;

            try
            {
                // Читаем сырой CF_UNICODETEXT напрямую из Win32 clipboard.
                // В отличие от Clipboard.GetText/TextDataFormat здесь не участвует
                // OLE/WinForms-конвертация форматов, поэтому CR/LF остаются ровно
                // такими, какими их положило приложение-источник.
                if (NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_UNICODETEXT))
                {
                    text = ReadUnicodeText();
                }
                else if (NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_TEXT))
                {
                    text = ReadAnsiText();
                }
                else
                {
                    return string.Empty;
                }
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }

            uint sequenceAfter = NativeMethods.GetClipboardSequenceNumber();

            // Если содержимое поменялось прямо во время чтения, не отдаём
            // промежуточную версию — повторяем чтение уже нового clipboard.
            if (sequenceBefore != 0 &&
                sequenceAfter != 0 &&
                sequenceBefore != sequenceAfter)
            {
                Thread.Sleep(RetryDelayMs);
                continue;
            }

            if (text is not null)
                return text;

            lastError = Marshal.GetLastWin32Error();
            Thread.Sleep(RetryDelayMs);
        }

        throw new Win32Exception(
            lastError,
            "Не удалось прочитать текущее содержимое буфера обмена. " +
            "Буфер занят другим процессом или источник ещё не завершил копирование.");
    }

    private static string? ReadUnicodeText()
    {
        IntPtr dataHandle = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);

        if (dataHandle == IntPtr.Zero)
            return null;

        IntPtr dataPointer = NativeMethods.GlobalLock(dataHandle);

        if (dataPointer == IntPtr.Zero)
            return null;

        try
        {
            return Marshal.PtrToStringUni(dataPointer);
        }
        finally
        {
            NativeMethods.GlobalUnlock(dataHandle);
        }
    }

    private static string? ReadAnsiText()
    {
        IntPtr dataHandle = NativeMethods.GetClipboardData(NativeMethods.CF_TEXT);

        if (dataHandle == IntPtr.Zero)
            return null;

        IntPtr dataPointer = NativeMethods.GlobalLock(dataHandle);

        if (dataPointer == IntPtr.Zero)
            return null;

        try
        {
            return Marshal.PtrToStringAnsi(dataPointer);
        }
        finally
        {
            NativeMethods.GlobalUnlock(dataHandle);
        }
    }
}
