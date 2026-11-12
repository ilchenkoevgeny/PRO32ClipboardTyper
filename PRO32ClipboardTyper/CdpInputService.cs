using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PRO32ClipboardTyper;

internal sealed class CdpInputService : IAsyncDisposable
{
    private const string DebugEndpoint = "http://127.0.0.1:9222/json/list";
    private const int ControlModifier = 2;
    private const int ShiftModifier = 8;
    private const int PrintableKeyDelayMs = 12;
    private const int TurboPrintableKeyDelayMs = 3;
    private const int TurboBatchSize = 24;
    private const int TurboBatchPauseMs = 8;
    private const int EnterDelayMs = 70;
    private const int TabDelayMs = 20;
    private const int BackspaceDelayMs = 20;
    private const int HomeDelayMs = 35;
    private const int IndentTabWidth = 4;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(3);

    private readonly ClientWebSocket _socket;
    private int _nextCommandId;

    private CdpInputService(ClientWebSocket socket)
    {
        _socket = socket;
    }

    public static async Task<CdpInputService> ConnectToSessionAsync(
        string windowTitle,
        CancellationToken cancellationToken)
    {
        List<CdpTarget> targets = await GetTargetsAsync(cancellationToken);
        CdpTarget? target = FindBestTarget(targets, windowTitle);

        if (target is null || string.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl))
        {
            throw new InvalidOperationException(
                "CDP-порт PRO32 доступен, но удалённая сессия среди Chromium targets не найдена. " +
                "Открой нужную сессию PRO32 и повтори ввод.");
        }

        ClientWebSocket socket = new();
        socket.Options.SetRequestHeader("Origin", "http://127.0.0.1:9222");

        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);

        try
        {
            await socket.ConnectAsync(new Uri(target.WebSocketDebuggerUrl), timeout.Token);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        CdpInputService service = new(socket);
        await service.BringPageToFrontAsync(cancellationToken);
        return service;
    }

    public static async Task<string> TestConnectionAsync(CancellationToken cancellationToken)
    {
        List<CdpTarget> targets = await GetTargetsAsync(cancellationToken);
        CdpTarget? session = targets.FirstOrDefault(IsSessionTarget);

        return session is null
            ? $"CDP отвечает. Targets: {targets.Count}, удалённая сессия не найдена."
            : $"CDP работает. Сессия: {session.Title}";
    }

    public static async Task<bool> IsPro32DebugEndpointAvailableAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            List<CdpTarget> targets = await GetTargetsAsync(cancellationToken);
            CdpTarget? target = targets.FirstOrDefault(IsSessionTarget);

            if (target is null || string.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl))
                return false;

            using ClientWebSocket socket = new();
            socket.Options.SetRequestHeader("Origin", "http://127.0.0.1:9222");

            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));

            await socket.ConnectAsync(new Uri(target.WebSocketDebuggerUrl), timeout.Token);
            bool isOpen = socket.State == WebSocketState.Open;
            socket.Abort();
            return isOpen;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task BringPageToFrontAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SendCommandAsync("Page.bringToFront", new { }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
        }
    }

    public Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        return SendTextCoreAsync(text, turbo: false, cancellationToken);
    }

    public Task SendTextTurboAsync(string text, CancellationToken cancellationToken)
    {
        return SendTextCoreAsync(text, turbo: true, cancellationToken);
    }

    private async Task SendTextCoreAsync(
        string text,
        bool turbo,
        CancellationToken cancellationToken)
    {
        await ReleaseStuckKeysAsync(cancellationToken);

        string normalizedText = NormalizeLineEndings(text);
        bool atLineStart = true;
        int queuedPrintableCharacters = 0;

        for (int characterIndex = 0; characterIndex < normalizedText.Length; characterIndex++)
        {
            char character = normalizedText[characterIndex];
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                switch (character)
                {
                    case '\n':
                        if (turbo && queuedPrintableCharacters > 0)
                        {
                            await FlushQueuedInputAsync(cancellationToken);
                            queuedPrintableCharacters = 0;
                        }

                        await SendSpecialKeyAsync(
                            "Enter", "Enter", 13,
                            turbo ? Math.Min(EnterDelayMs, 30) : EnterDelayMs,
                            cancellationToken);

                        await SendSpecialKeyAsync(
                            "Home", "Home", 0x24,
                            turbo ? Math.Min(HomeDelayMs, 15) : HomeDelayMs,
                            cancellationToken);

                        atLineStart = true;
                        break;

                    case '\t' when atLineStart:
                        for (int i = 0; i < IndentTabWidth; i++)
                        {
                            if (turbo)
                            {
                                await QueuePrintableCharacterAsync(' ', cancellationToken);
                                queuedPrintableCharacters++;

                                if (queuedPrintableCharacters >= TurboBatchSize)
                                {
                                    await FlushQueuedInputAsync(cancellationToken);
                                    queuedPrintableCharacters = 0;
                                    await Task.Delay(TurboBatchPauseMs, cancellationToken);
                                }
                                else
                                {
                                    await Task.Delay(TurboPrintableKeyDelayMs, cancellationToken);
                                }
                            }
                            else
                            {
                                await SendPrintableCharacterAsync(' ', cancellationToken);
                            }
                        }

                        break;

                    case '\t':
                        if (turbo && queuedPrintableCharacters > 0)
                        {
                            await FlushQueuedInputAsync(cancellationToken);
                            queuedPrintableCharacters = 0;
                        }

                        await SendSpecialKeyAsync(
                            "Tab", "Tab", 9,
                            turbo ? Math.Min(TabDelayMs, 10) : TabDelayMs,
                            cancellationToken);

                        atLineStart = false;
                        break;

                    case '\b':
                        if (turbo && queuedPrintableCharacters > 0)
                        {
                            await FlushQueuedInputAsync(cancellationToken);
                            queuedPrintableCharacters = 0;
                        }

                        await SendSpecialKeyAsync(
                            "Backspace", "Backspace", 8,
                            turbo ? Math.Min(BackspaceDelayMs, 10) : BackspaceDelayMs,
                            cancellationToken);
                        break;

                    default:
                        if (turbo)
                        {
                            await QueuePrintableCharacterAsync(character, cancellationToken);
                            queuedPrintableCharacters++;

                            if (queuedPrintableCharacters >= TurboBatchSize)
                            {
                                await FlushQueuedInputAsync(cancellationToken);
                                queuedPrintableCharacters = 0;
                                await Task.Delay(TurboBatchPauseMs, cancellationToken);
                            }
                            else
                            {
                                await Task.Delay(TurboPrintableKeyDelayMs, cancellationToken);
                            }
                        }
                        else
                        {
                            await SendPrintableCharacterAsync(character, cancellationToken);
                        }

                        if (character != ' ')
                            atLineStart = false;

                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                string visibleCharacter = character switch
                {
                    '\n' => "<LF>",
                    '\t' => "<TAB>",
                    '\b' => "<BACKSPACE>",
                    ' ' => "<SPACE>",
                    _ => character.ToString()
                };

                throw new InvalidOperationException(
                    $"Ошибка {(turbo ? "Turbo-" : string.Empty)}передачи символа " +
                    $"{characterIndex + 1} из {normalizedText.Length}: " +
                    $"{visibleCharacter} (U+{(int)character:X4}). " +
                    "Текст до этого символа уже мог быть введён.",
                    ex);
            }
        }

        if (turbo && queuedPrintableCharacters > 0)
            await FlushQueuedInputAsync(cancellationToken);
    }

    public async Task SendClipboardPasteAsync(CancellationToken cancellationToken)
    {
        await ReleaseStuckKeysAsync(cancellationToken);
        await SendControlShortcutAsync("v", "KeyV", 0x56, cancellationToken);
    }

    public async Task SendClipboardCopyAsync(CancellationToken cancellationToken)
    {
        await ReleaseStuckKeysAsync(cancellationToken);
        await SendControlShortcutAsync("c", "KeyC", 0x43, cancellationToken);
    }

    private async Task SendControlShortcutAsync(
        string key,
        string code,
        int virtualKeyCode,
        CancellationToken cancellationToken)
    {
        await DispatchRawKeyEventAsync(
            "rawKeyDown", "Control", "ControlLeft", 0x11,
            ControlModifier, cancellationToken);

        try
        {
            await DispatchRawKeyEventAsync(
                "rawKeyDown", key, code, virtualKeyCode,
                ControlModifier, cancellationToken);

            await Task.Delay(25, cancellationToken);

            await DispatchRawKeyEventAsync(
                "keyUp", key, code, virtualKeyCode,
                ControlModifier, cancellationToken);
        }
        finally
        {
            try
            {
                await DispatchRawKeyEventAsync(
                    "keyUp", "Control", "ControlLeft", 0x11,
                    0, cancellationToken);
            }
            catch when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        await Task.Delay(80, cancellationToken);
    }

    private async Task ReleaseStuckKeysAsync(CancellationToken cancellationToken)
    {
        await DispatchRawKeyEventAsync("keyUp", "Control", "ControlLeft", 0x11, 0, cancellationToken);
        await DispatchRawKeyEventAsync("keyUp", "Shift", "ShiftLeft", 0x10, 0, cancellationToken);
        await DispatchRawKeyEventAsync("keyUp", "Alt", "AltLeft", 0x12, 0, cancellationToken);
        await DispatchRawKeyEventAsync("keyUp", "Meta", "MetaLeft", 0x5B, 0, cancellationToken);
        await Task.Delay(20, cancellationToken);
    }

    private async Task SendPrintableCharacterAsync(
        char character,
        CancellationToken cancellationToken)
    {
        KeyDescriptor descriptor = GetKeyDescriptor(character);
        string key = character.ToString();

        await DispatchRawKeyEventAsync(
            "rawKeyDown", key, descriptor.Code,
            descriptor.VirtualKeyCode, descriptor.Modifiers,
            cancellationToken);

        await DispatchRawKeyEventAsync(
            "keyUp", key, descriptor.Code,
            descriptor.VirtualKeyCode, descriptor.Modifiers,
            cancellationToken);

        await Task.Delay(PrintableKeyDelayMs, cancellationToken);
    }

    private async Task QueuePrintableCharacterAsync(
        char character,
        CancellationToken cancellationToken)
    {
        KeyDescriptor descriptor = GetKeyDescriptor(character);
        string key = character.ToString();

        await QueueRawKeyEventAsync(
            "rawKeyDown", key, descriptor.Code,
            descriptor.VirtualKeyCode, descriptor.Modifiers,
            cancellationToken);

        await QueueRawKeyEventAsync(
            "keyUp", key, descriptor.Code,
            descriptor.VirtualKeyCode, descriptor.Modifiers,
            cancellationToken);
    }

    private async Task SendSpecialKeyAsync(
        string key,
        string code,
        int virtualKeyCode,
        int delayAfterKeyMs,
        CancellationToken cancellationToken)
    {
        await DispatchRawKeyEventAsync(
            "rawKeyDown", key, code, virtualKeyCode, 0, cancellationToken);
        await DispatchRawKeyEventAsync(
            "keyUp", key, code, virtualKeyCode, 0, cancellationToken);

        await Task.Delay(delayAfterKeyMs, cancellationToken);
    }

    private Task DispatchRawKeyEventAsync(
        string type,
        string key,
        string code,
        int virtualKeyCode,
        int modifiers,
        CancellationToken cancellationToken)
    {
        return SendCommandAsync(
            "Input.dispatchKeyEvent",
            BuildKeyEvent(type, key, code, virtualKeyCode, modifiers),
            cancellationToken);
    }

    private Task QueueRawKeyEventAsync(
        string type,
        string key,
        string code,
        int virtualKeyCode,
        int modifiers,
        CancellationToken cancellationToken)
    {
        return QueueCommandAsync(
            "Input.dispatchKeyEvent",
            BuildKeyEvent(type, key, code, virtualKeyCode, modifiers),
            cancellationToken);
    }

    private static object BuildKeyEvent(
        string type,
        string key,
        string code,
        int virtualKeyCode,
        int modifiers)
    {
        return new
        {
            type,
            modifiers,
            key,
            code,
            windowsVirtualKeyCode = virtualKeyCode,
            nativeVirtualKeyCode = virtualKeyCode,
            autoRepeat = false,
            isKeypad = false,
            isSystemKey = false,
            location = 0
        };
    }

    private async Task QueueCommandAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        int commandId = Interlocked.Increment(ref _nextCommandId);
        string json = JsonSerializer.Serialize(new
        {
            id = commandId,
            method,
            @params = parameters
        });

        byte[] payload = Encoding.UTF8.GetBytes(json);

        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);

        await _socket.SendAsync(
            payload,
            WebSocketMessageType.Text,
            endOfMessage: true,
            timeout.Token);
    }

    private Task FlushQueuedInputAsync(CancellationToken cancellationToken)
    {
        return DispatchRawKeyEventAsync(
            "keyUp", "Shift", "ShiftLeft", 0x10, 0, cancellationToken);
    }

    private async Task SendCommandAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        _ = await SendCommandForResultAsync(method, parameters, cancellationToken);
    }

    private async Task<JsonElement> SendCommandForResultAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        int commandId = Interlocked.Increment(ref _nextCommandId);

        string json = JsonSerializer.Serialize(new
        {
            id = commandId,
            method,
            @params = parameters
        });

        byte[] payload = Encoding.UTF8.GetBytes(json);

        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);

        try
        {
            await _socket.SendAsync(
                payload,
                WebSocketMessageType.Text,
                endOfMessage: true,
                timeout.Token);

            while (true)
            {
                string response = await ReceiveTextMessageAsync(timeout.Token);

                using JsonDocument document = JsonDocument.Parse(response);
                JsonElement root = document.RootElement;

                if (!root.TryGetProperty("id", out JsonElement idElement) ||
                    idElement.GetInt32() != commandId)
                {
                    continue;
                }

                if (root.TryGetProperty("error", out JsonElement errorElement))
                {
                    throw new InvalidOperationException(
                        $"Chromium отклонил команду {method}: {errorElement}");
                }

                if (root.TryGetProperty("result", out JsonElement resultElement))
                    return resultElement.Clone();

                return default;
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            Abort();

            throw new TimeoutException(
                $"PRO32/CDP не ответил на команду {method} за " +
                $"{CommandTimeout.TotalSeconds:0} сек.",
                ex);
        }
    }

    private async Task<string> ReceiveTextMessageAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8192];
        using MemoryStream stream = new();

        while (true)
        {
            WebSocketReceiveResult result = await _socket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("Chromium закрыл CDP-соединение с PRO32.");

            stream.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    private static async Task<List<CdpTarget>> GetTargetsAsync(
        CancellationToken cancellationToken)
    {
        using HttpClient httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(2)
        };

        try
        {
            string json = await httpClient.GetStringAsync(DebugEndpoint, cancellationToken);
            return JsonSerializer.Deserialize<List<CdpTarget>>(json) ?? [];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException(
                "Не удалось подключиться к Chromium PRO32 на 127.0.0.1:9222. " +
                "Если ошибка повторяется, проверь, не занят ли порт 9222 другим приложением.",
                ex);
        }
    }

    private static CdpTarget? FindBestTarget(
        IEnumerable<CdpTarget> targets,
        string windowTitle)
    {
        List<CdpTarget> sessions = targets.Where(IsSessionTarget).ToList();

        if (sessions.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(windowTitle))
        {
            CdpTarget? exact = sessions.FirstOrDefault(
                x => x.Title.Equals(windowTitle, StringComparison.OrdinalIgnoreCase));

            if (exact is not null)
                return exact;

            string stableTitle = RemoveLeadingTimer(windowTitle);

            CdpTarget? stable = sessions.FirstOrDefault(
                x => RemoveLeadingTimer(x.Title)
                    .Equals(stableTitle, StringComparison.OrdinalIgnoreCase));

            if (stable is not null)
                return stable;
        }

        return sessions
            .OrderByDescending(GetSessionTargetScore)
            .FirstOrDefault();
    }

    private static bool IsSessionTarget(CdpTarget target)
    {
        if (!target.Type.Equals("page", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl))
        {
            return false;
        }

        if (target.Title.Contains(
            "PRO32 Connect Dashboard",
            StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !target.Url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase) &&
               !target.Url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetSessionTargetScore(CdpTarget target)
    {
        int score = 0;

        if (target.Title.Contains("PRO32 Connect", StringComparison.OrdinalIgnoreCase))
            score += 20;

        if (target.Url.Contains("remote", StringComparison.OrdinalIgnoreCase) ||
            target.Url.Contains("session", StringComparison.OrdinalIgnoreCase) ||
            target.Url.Contains("control", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        if (target.Url.Contains("dashboard", StringComparison.OrdinalIgnoreCase) ||
            target.Url.Contains("settings", StringComparison.OrdinalIgnoreCase))
        {
            score -= 20;
        }

        return score;
    }

    private static string RemoveLeadingTimer(string title)
    {
        int separatorIndex = title.IndexOf(" - ", StringComparison.Ordinal);

        if (separatorIndex <= 0)
            return title.Trim();

        string prefix = title[..separatorIndex];
        string[] parts = prefix.Split(':');

        if ((parts.Length == 2 || parts.Length == 3) &&
            parts.All(x => x.Length is >= 1 and <= 2 && x.All(char.IsDigit)))
        {
            return title[(separatorIndex + 3)..].Trim();
        }

        return title.Trim();
    }

    private static string NormalizeLineEndings(string text)
    {
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\u0085', '\n')
            .Replace('\u2028', '\n')
            .Replace('\u2029', '\n')
            .Replace('\u00A0', ' ')
            .Replace('\u2007', ' ')
            .Replace('\u202F', ' ')
            .Replace("\uFEFF", string.Empty, StringComparison.Ordinal)
            .Replace("\u200B", string.Empty, StringComparison.Ordinal)
            .Replace("\u2060", string.Empty, StringComparison.Ordinal);
    }

    private static KeyDescriptor GetKeyDescriptor(char character)
    {
        if (character is >= 'a' and <= 'z')
        {
            char upper = char.ToUpperInvariant(character);
            return new KeyDescriptor($"Key{upper}", upper, 0);
        }

        if (character is >= 'A' and <= 'Z')
            return new KeyDescriptor($"Key{character}", character, ShiftModifier);

        if (character is >= '0' and <= '9')
            return new KeyDescriptor($"Digit{character}", character, 0);

        if (TryGetUsPunctuationDescriptor(character, out KeyDescriptor punctuation))
            return punctuation;

        if (TryGetRussianDescriptor(character, out KeyDescriptor russian))
            return russian;

        if (character == ' ')
            return new KeyDescriptor("Space", 0x20, 0);

        return new KeyDescriptor(string.Empty, 0, 0);
    }

    private static bool TryGetUsPunctuationDescriptor(
        char character,
        out KeyDescriptor descriptor)
    {
        descriptor = character switch
        {
            '-' => new("Minus", 0xBD, 0),
            '_' => new("Minus", 0xBD, ShiftModifier),
            '=' => new("Equal", 0xBB, 0),
            '+' => new("Equal", 0xBB, ShiftModifier),
            '[' => new("BracketLeft", 0xDB, 0),
            '{' => new("BracketLeft", 0xDB, ShiftModifier),
            ']' => new("BracketRight", 0xDD, 0),
            '}' => new("BracketRight", 0xDD, ShiftModifier),
            '\\' => new("Backslash", 0xDC, 0),
            '|' => new("Backslash", 0xDC, ShiftModifier),
            ';' => new("Semicolon", 0xBA, 0),
            ':' => new("Semicolon", 0xBA, ShiftModifier),
            '\'' => new("Quote", 0xDE, 0),
            '"' => new("Quote", 0xDE, ShiftModifier),
            ',' => new("Comma", 0xBC, 0),
            '<' => new("Comma", 0xBC, ShiftModifier),
            '.' => new("Period", 0xBE, 0),
            '>' => new("Period", 0xBE, ShiftModifier),
            '/' => new("Slash", 0xBF, 0),
            '?' => new("Slash", 0xBF, ShiftModifier),
            '\u0060' => new("Backquote", 0xC0, 0),
            '~' => new("Backquote", 0xC0, ShiftModifier),
            '!' => new("Digit1", 0x31, ShiftModifier),
            '@' => new("Digit2", 0x32, ShiftModifier),
            '#' => new("Digit3", 0x33, ShiftModifier),
            '$' => new("Digit4", 0x34, ShiftModifier),
            '%' => new("Digit5", 0x35, ShiftModifier),
            '^' => new("Digit6", 0x36, ShiftModifier),
            '&' => new("Digit7", 0x37, ShiftModifier),
            '*' => new("Digit8", 0x38, ShiftModifier),
            '(' => new("Digit9", 0x39, ShiftModifier),
            ')' => new("Digit0", 0x30, ShiftModifier),
            _ => default
        };

        return !string.IsNullOrEmpty(descriptor.Code);
    }

    private static bool TryGetRussianDescriptor(
        char character,
        out KeyDescriptor descriptor)
    {
        char lower = char.ToLowerInvariant(character);
        int modifiers = char.IsUpper(character) ? ShiftModifier : 0;

        (string Code, int Vk)? mapping = lower switch
        {
            'й' => ("KeyQ", 0x51), 'ц' => ("KeyW", 0x57), 'у' => ("KeyE", 0x45),
            'к' => ("KeyR", 0x52), 'е' => ("KeyT", 0x54), 'н' => ("KeyY", 0x59),
            'г' => ("KeyU", 0x55), 'ш' => ("KeyI", 0x49), 'щ' => ("KeyO", 0x4F),
            'з' => ("KeyP", 0x50), 'х' => ("BracketLeft", 0xDB), 'ъ' => ("BracketRight", 0xDD),
            'ф' => ("KeyA", 0x41), 'ы' => ("KeyS", 0x53), 'в' => ("KeyD", 0x44),
            'а' => ("KeyF", 0x46), 'п' => ("KeyG", 0x47), 'р' => ("KeyH", 0x48),
            'о' => ("KeyJ", 0x4A), 'л' => ("KeyK", 0x4B), 'д' => ("KeyL", 0x4C),
            'ж' => ("Semicolon", 0xBA), 'э' => ("Quote", 0xDE),
            'я' => ("KeyZ", 0x5A), 'ч' => ("KeyX", 0x58), 'с' => ("KeyC", 0x43),
            'м' => ("KeyV", 0x56), 'и' => ("KeyB", 0x42), 'т' => ("KeyN", 0x4E),
            'ь' => ("KeyM", 0x4D), 'б' => ("Comma", 0xBC), 'ю' => ("Period", 0xBE),
            'ё' => ("Backquote", 0xC0),
            _ => null
        };

        if (mapping is null)
        {
            descriptor = default;
            return false;
        }

        descriptor = new KeyDescriptor(
            mapping.Value.Code,
            mapping.Value.Vk,
            modifiers);

        return true;
    }

    public void Abort()
    {
        try
        {
            _socket.Abort();
        }
        catch
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        Abort();
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }

    private readonly record struct KeyDescriptor(
        string Code,
        int VirtualKeyCode,
        int Modifiers);

    private sealed class CdpTarget
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("webSocketDebuggerUrl")]
        public string WebSocketDebuggerUrl { get; set; } = string.Empty;
    }
}
