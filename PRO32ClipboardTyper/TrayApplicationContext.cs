using System.ComponentModel;

namespace PRO32ClipboardTyper;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _applicationIcon;
    private readonly ToolStripMenuItem _pasteMenuItem;
    private readonly ToolStripMenuItem _turboPasteMenuItem;
    private readonly ToolStripMenuItem _fastPasteMenuItem;
    private readonly ToolStripMenuItem _receiveClipboardMenuItem;
    private readonly ToolStripMenuItem _stopMenuItem;
    private readonly System.Windows.Forms.Timer _hotKeyFallbackTimer;
    private readonly System.Windows.Forms.Timer _startupTimer;

    private HotKeyWindow? _hotKeyWindow;
    private CancellationTokenSource? _typingCancellation;
    private CdpInputService? _activeCdp;
    private bool _isTyping;
    private bool _clipboardOperationInProgress;
    private bool _pasteChordWasDown;
    private bool _stopChordWasDown;

    public TrayApplicationContext()
    {
        _pasteMenuItem = new ToolStripMenuItem(
            "Legacy: ввести буфер посимвольно    Ctrl+Shift+V",
            null,
            async (_, _) => await PasteClipboardIntoPro32Async());

        _turboPasteMenuItem = new ToolStripMenuItem(
            "Turbo: быстрый клавиатурный ввод для RDP",
            null,
            async (_, _) => await TurboPasteClipboardIntoPro32Async());

        _fastPasteMenuItem = new ToolStripMenuItem(
            "Отправить буфер на первый удалённый ПК",
            null,
            async (_, _) => await FastPasteClipboardIntoPro32Async());

        _receiveClipboardMenuItem = new ToolStripMenuItem(
            "Получить буфер с первого удалённого ПК",
            null,
            async (_, _) => await ReceiveClipboardFromPro32Async());

        _stopMenuItem = new ToolStripMenuItem(
            "Остановить ввод    Ctrl+Shift+C",
            null,
            (_, _) => StopTyping(showIdleMessage: true))
        {
            Enabled = false
        };

        ToolStripMenuItem pro32ClipboardMenuItem =
            new("Буфер PRO32 (только первый удалённый ПК)");

        pro32ClipboardMenuItem.DropDownItems.Add(_fastPasteMenuItem);
        pro32ClipboardMenuItem.DropDownItems.Add(_receiveClipboardMenuItem);

        ContextMenuStrip contextMenu = new();
        contextMenu.Items.Add(_pasteMenuItem);
        contextMenu.Items.Add(_turboPasteMenuItem);
        contextMenu.Items.Add(_stopMenuItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(pro32ClipboardMenuItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(new ToolStripMenuItem(
            "Проверить CDP-соединение PRO32",
            null,
            async (_, _) => await CheckCdpAsync()));
        contextMenu.Items.Add(new ToolStripMenuItem(
            "Найти и активировать PRO32",
            null,
            (_, _) => FindAndActivatePro32()));
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(new ToolStripMenuItem(
            "Выход",
            null,
            (_, _) => ExitThread()));

        _applicationIcon = LoadApplicationIcon();

        _notifyIcon = new NotifyIcon
        {
            Icon = _applicationIcon,
            Text = "PRO32 Clipboard Typer (CDP)",
            ContextMenuStrip = contextMenu,
            Visible = true
        };

        // Дефолт остаётся Legacy.
        _notifyIcon.DoubleClick += async (_, _) => await PasteClipboardIntoPro32Async();

        _hotKeyFallbackTimer = new System.Windows.Forms.Timer
        {
            Interval = 30
        };
        _hotKeyFallbackTimer.Tick += HotKeyFallbackTimer_Tick;
        _hotKeyFallbackTimer.Start();

        try
        {
            _hotKeyWindow = new HotKeyWindow();
            _hotKeyWindow.PasteHotKeyPressed +=
                async (_, _) => await PasteClipboardIntoPro32Async();
            _hotKeyWindow.StopHotKeyPressed +=
                (_, _) => StopTyping(showIdleMessage: false);
        }
        catch (Win32Exception ex)
        {
            MessageBox.Show(
                ex.Message + Environment.NewLine + Environment.NewLine +
                "Приложение останется в трее, но глобальный хоткей работать не будет.",
                "PRO32 Clipboard Typer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        _startupTimer = new System.Windows.Forms.Timer
        {
            Interval = 100
        };
        _startupTimer.Tick += StartupTimer_Tick;
        _startupTimer.Start();
    }

    private async void StartupTimer_Tick(object? sender, EventArgs e)
    {
        _startupTimer.Stop();
        await EnsurePro32ReadyAsync(showStatus: true);
    }

    private Task PasteClipboardIntoPro32Async()
    {
        return TypeClipboardAsync(turbo: false);
    }

    private Task TurboPasteClipboardIntoPro32Async()
    {
        return TypeClipboardAsync(turbo: true);
    }

    private async Task TypeClipboardAsync(bool turbo)
    {
        if (_isTyping || _clipboardOperationInProgress)
            return;

        _isTyping = true;
        SetAllOperationMenuState(enabled: false);
        _stopMenuItem.Enabled = true;
        _typingCancellation = new CancellationTokenSource();

        try
        {
            try
            {
                _hotKeyWindow?.EnableStopHotKey();
            }
            catch (Win32Exception ex)
            {
                ShowWarning(ex.Message);
            }

            string text = ClipboardService.GetText();

            if (string.IsNullOrEmpty(text))
            {
                ShowWarning("В буфере обмена нет текста.");
                return;
            }

            if (!await EnsurePro32ReadyAsync(showStatus: true))
                return;

            IntPtr targetWindow = PRO32WindowService.FindSessionWindow();
            string targetTitle = targetWindow != IntPtr.Zero
                ? PRO32WindowService.GetWindowTitle(targetWindow)
                : string.Empty;

            if (targetWindow != IntPtr.Zero &&
                NativeMethods.GetForegroundWindow() != targetWindow)
            {
                _ = PRO32WindowService.ActivateWindow(targetWindow);
            }

            await WaitForHotKeyReleaseAsync(_typingCancellation.Token);

            CancellationToken cancellationToken = _typingCancellation.Token;

            await using CdpInputService cdp =
                await CdpInputService.ConnectToSessionAsync(
                    targetTitle,
                    cancellationToken);

            _activeCdp = cdp;

            try
            {
                if (turbo)
                    await cdp.SendTextTurboAsync(text, cancellationToken);
                else
                    await cdp.SendTextAsync(text, cancellationToken);
            }
            finally
            {
                if (ReferenceEquals(_activeCdp, cdp))
                    _activeCdp = null;
            }

            _notifyIcon.ShowBalloonTip(
                1400,
                "PRO32 Clipboard Typer",
                turbo ? "Turbo-ввод завершён." : "Ввод завершён.",
                ToolTipIcon.Info);
        }
        catch (OperationCanceledException)
        {
            _notifyIcon.ShowBalloonTip(
                1200,
                "PRO32 Clipboard Typer",
                turbo ? "Turbo-ввод остановлен." : "Ввод остановлен.",
                ToolTipIcon.Info);
        }
        catch (Exception) when (_typingCancellation?.IsCancellationRequested == true)
        {
            _notifyIcon.ShowBalloonTip(
                1200,
                "PRO32 Clipboard Typer",
                turbo ? "Turbo-ввод остановлен." : "Ввод остановлен.",
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                turbo ? "PRO32 Clipboard Typer — Turbo" : "PRO32 Clipboard Typer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _activeCdp = null;
            _hotKeyWindow?.DisableStopHotKey();

            _typingCancellation?.Dispose();
            _typingCancellation = null;

            _isTyping = false;
            SetAllOperationMenuState(enabled: true);
            _stopMenuItem.Enabled = false;
        }
    }

    private async Task FastPasteClipboardIntoPro32Async()
    {
        if (_isTyping || _clipboardOperationInProgress)
            return;

        _clipboardOperationInProgress = true;
        SetAllOperationMenuState(enabled: false);

        try
        {
            string text = ClipboardService.GetText();

            if (string.IsNullOrEmpty(text))
            {
                ShowWarning("В локальном буфере обмена нет текста.");
                return;
            }

            if (!await EnsurePro32ReadyAsync(showStatus: true))
                return;

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));

            await using CdpInputService cdp =
                await ConnectToCurrentSessionAsync(timeout.Token);

            // Это штатный clipboard PRO32 и только первый удалённый ПК.
            // Запрет clipboard redirection во вложенной RDP-сессии он не обходит.
            await cdp.SendClipboardPasteAsync(timeout.Token);

            _notifyIcon.ShowBalloonTip(
                1800,
                "PRO32 Clipboard Typer",
                "Выполнена вставка через буфер PRO32 (первый удалённый ПК).",
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Буфер PRO32",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _clipboardOperationInProgress = false;
            SetAllOperationMenuState(enabled: true);
        }
    }

    private async Task ReceiveClipboardFromPro32Async()
    {
        if (_isTyping || _clipboardOperationInProgress)
            return;

        _clipboardOperationInProgress = true;
        SetAllOperationMenuState(enabled: false);

        try
        {
            if (!await EnsurePro32ReadyAsync(showStatus: true))
                return;

            uint sequenceBefore = NativeMethods.GetClipboardSequenceNumber();

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));

            await using CdpInputService cdp =
                await ConnectToCurrentSessionAsync(timeout.Token);

            // Ctrl+C может синхронизировать clipboard только там, где это разрешено.
            // Если фокус находится во вложенном RDP с запрещённым redirection,
            // clipboard второй машины наружу не выйдет.
            await cdp.SendClipboardCopyAsync(timeout.Token);

            bool changed = await WaitForClipboardChangeAsync(
                sequenceBefore,
                TimeSpan.FromSeconds(3),
                timeout.Token);

            if (!changed)
            {
                ShowWarning(
                    "Локальный буфер не изменился. Если фокус находится во вложенном RDP " +
                    "с запрещённым clipboard redirection — это ожидаемо.");
                return;
            }

            string text = ClipboardService.GetText();

            _notifyIcon.ShowBalloonTip(
                2200,
                "Буфер получен",
                string.IsNullOrEmpty(text)
                    ? "Буфер синхронизирован, но текста в нём нет."
                    : $"Получено текста: {text.Length:N0} символов.",
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Получение буфера PRO32",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _clipboardOperationInProgress = false;
            SetAllOperationMenuState(enabled: true);
        }
    }

    private static async Task<CdpInputService> ConnectToCurrentSessionAsync(
        CancellationToken cancellationToken)
    {
        IntPtr targetWindow = PRO32WindowService.FindSessionWindow();
        string targetTitle = targetWindow != IntPtr.Zero
            ? PRO32WindowService.GetWindowTitle(targetWindow)
            : string.Empty;

        if (targetWindow != IntPtr.Zero &&
            NativeMethods.GetForegroundWindow() != targetWindow)
        {
            _ = PRO32WindowService.ActivateWindow(targetWindow);
        }

        return await CdpInputService.ConnectToSessionAsync(
            targetTitle,
            cancellationToken);
    }

    private static async Task<bool> WaitForClipboardChangeAsync(
        uint sequenceBefore,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            uint currentSequence = NativeMethods.GetClipboardSequenceNumber();

            if (currentSequence != 0 && currentSequence != sequenceBefore)
                return true;

            await Task.Delay(40, cancellationToken);
        }

        return false;
    }

    private void SetAllOperationMenuState(bool enabled)
    {
        bool value = enabled && !_isTyping && !_clipboardOperationInProgress;
        _pasteMenuItem.Enabled = value;
        _turboPasteMenuItem.Enabled = value;
        _fastPasteMenuItem.Enabled = value;
        _receiveClipboardMenuItem.Enabled = value;
    }

    private void HotKeyFallbackTimer_Tick(object? sender, EventArgs e)
    {
        bool controlPressed = IsKeyPressed(NativeMethods.VK_CONTROL);
        bool shiftPressed = IsKeyPressed(NativeMethods.VK_SHIFT);
        bool vPressed = IsKeyPressed(NativeMethods.VK_V);
        bool cPressed = IsKeyPressed(NativeMethods.VK_C);

        bool pasteChordDown = controlPressed && shiftPressed && vPressed;
        bool stopChordDown = controlPressed && shiftPressed && cPressed;

        if (pasteChordDown &&
            !_pasteChordWasDown &&
            !_isTyping &&
            !_clipboardOperationInProgress)
        {
            _ = PasteClipboardIntoPro32Async();
        }

        if (stopChordDown && !_stopChordWasDown && _isTyping)
            StopTyping(showIdleMessage: false);

        _pasteChordWasDown = pasteChordDown;
        _stopChordWasDown = stopChordDown;
    }

    private static bool IsKeyPressed(int virtualKey)
    {
        return (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private async Task CheckCdpAsync()
    {
        try
        {
            if (!await EnsurePro32ReadyAsync(showStatus: true))
                return;

            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(4));

            string result =
                await CdpInputService.TestConnectionAsync(timeout.Token);

            MessageBox.Show(
                result,
                "Проверка PRO32 CDP",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "PRO32 CDP недоступен",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private async Task<bool> EnsurePro32ReadyAsync(bool showStatus)
    {
        try
        {
            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(25));

            PRO32ProcessService.EnsureResult result =
                await PRO32ProcessService.EnsureRunningWithCdpAsync(
                    timeout.Token);

            if (showStatus && result.WasRestarted)
            {
                _notifyIcon.ShowBalloonTip(
                    3500,
                    "PRO32 перезапущен",
                    "Dashboard был запущен без рабочего CDP и перезапущен автоматически. " +
                    "Если удалённая сессия была открыта, подключись к ней заново.",
                    ToolTipIcon.Info);
            }
            else if (showStatus && result.WasStarted)
            {
                _notifyIcon.ShowBalloonTip(
                    2500,
                    "PRO32 запущен",
                    "Dashboard автоматически запущен с CDP.",
                    ToolTipIcon.Info);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show(
                "Истекло время ожидания запуска PRO32 Connect Dashboard.",
                "PRO32 Clipboard Typer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Не удалось подготовить PRO32",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            return false;
        }
    }

    private static async Task WaitForHotKeyReleaseAsync(
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool controlPressed = IsKeyPressed(NativeMethods.VK_CONTROL);
            bool shiftPressed = IsKeyPressed(NativeMethods.VK_SHIFT);
            bool vPressed = IsKeyPressed(NativeMethods.VK_V);

            if (!controlPressed && !shiftPressed && !vPressed)
                break;

            await Task.Delay(10, cancellationToken);
        }

        await Task.Delay(80, cancellationToken);
    }

    private void StopTyping(bool showIdleMessage)
    {
        if (!_isTyping || _typingCancellation is null)
        {
            if (showIdleMessage)
            {
                _notifyIcon.ShowBalloonTip(
                    1200,
                    "PRO32 Clipboard Typer",
                    "Сейчас ввод не выполняется.",
                    ToolTipIcon.Info);
            }

            return;
        }

        _typingCancellation.Cancel();
        _activeCdp?.Abort();
    }

    private void FindAndActivatePro32()
    {
        IntPtr targetWindow = PRO32WindowService.FindSessionWindow();

        if (targetWindow == IntPtr.Zero)
        {
            ShowWarning("Окно удалённой сессии PRO32 Connect не найдено.");
            return;
        }

        if (!PRO32WindowService.ActivateWindow(targetWindow))
        {
            ShowWarning("Окно найдено, но не удалось его активировать.");
            return;
        }

        _notifyIcon.ShowBalloonTip(
            1500,
            "PRO32 найден",
            PRO32WindowService.GetWindowTitle(targetWindow),
            ToolTipIcon.Info);
    }

    private void ShowWarning(string text)
    {
        _notifyIcon.ShowBalloonTip(
            2800,
            "PRO32 Clipboard Typer",
            text,
            ToolTipIcon.Warning);
    }

    private static Icon LoadApplicationIcon()
    {
        Icon? pro32Icon = PRO32ProcessService.TryExtractApplicationIcon();

        if (pro32Icon is not null)
            return pro32Icon;

        return (Icon)SystemIcons.Application.Clone();
    }

    protected override void ExitThreadCore()
    {
        _typingCancellation?.Cancel();
        _activeCdp?.Abort();
        _activeCdp = null;

        _typingCancellation?.Dispose();
        _typingCancellation = null;

        _startupTimer.Stop();
        _startupTimer.Dispose();

        _hotKeyFallbackTimer.Stop();
        _hotKeyFallbackTimer.Dispose();

        _hotKeyWindow?.Dispose();
        _hotKeyWindow = null;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _applicationIcon.Dispose();

        base.ExitThreadCore();
    }
}
