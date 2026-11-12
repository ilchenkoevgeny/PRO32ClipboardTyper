using System.Diagnostics;

namespace PRO32ClipboardTyper;

internal static class PRO32ProcessService
{
    private const string DashboardExeName = "PRO32 Connect Dashboard.exe";
    private const string DashboardProcessName = "PRO32 Connect Dashboard";
    private const string RemoteDebuggingArguments =
        "--remote-debugging-port=9222 " +
        "--remote-debugging-address=127.0.0.1 " +
        "--remote-allow-origins=http://127.0.0.1:9222";

    private static readonly TimeSpan ExistingProcessGracePeriod = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private static readonly SemaphoreSlim EnsureSemaphore = new(1, 1);

    public static string ExecutablePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "pro32connect.dashboard.electron",
        DashboardExeName);

    public static async Task<EnsureResult> EnsureRunningWithCdpAsync(
        CancellationToken cancellationToken)
    {
        await EnsureSemaphore.WaitAsync(cancellationToken);

        try
        {
            List<Process> processes = GetDashboardProcesses();

            try
            {
                if (processes.Count > 0)
                {
                    if (await IsCdpReadyAsync(cancellationToken))
                    {
                        return new EnsureResult(
                            WasStarted: false,
                            WasRestarted: false,
                            ExecutablePath: ResolveExecutablePath(processes));
                    }

                    // Если PRO32 только что запущен обычным способом, даём ему немного
                    // времени на инициализацию CDP. Это не позволяет Typer убивать
                    // корректно запущенный Dashboard на самом старте из-за гонки.
                    await Task.Delay(ExistingProcessGracePeriod, cancellationToken);

                    if (await IsCdpReadyAsync(cancellationToken))
                    {
                        return new EnsureResult(
                            WasStarted: false,
                            WasRestarted: false,
                            ExecutablePath: ResolveExecutablePath(processes));
                    }

                    string executablePath = ResolveExecutablePath(processes);

                    await StopDashboardProcessesAsync(processes, cancellationToken);

                    StartDashboard(executablePath);
                    await WaitForCdpAsync(cancellationToken);

                    return new EnsureResult(
                        WasStarted: true,
                        WasRestarted: true,
                        ExecutablePath: executablePath);
                }

                string defaultExecutablePath = ExecutablePath;

                StartDashboard(defaultExecutablePath);
                await WaitForCdpAsync(cancellationToken);

                return new EnsureResult(
                    WasStarted: true,
                    WasRestarted: false,
                    ExecutablePath: defaultExecutablePath);
            }
            finally
            {
                DisposeProcesses(processes);
            }
        }
        finally
        {
            EnsureSemaphore.Release();
        }
    }

    public static Icon? TryExtractApplicationIcon()
    {
        string executablePath = ExecutablePath;

        if (!File.Exists(executablePath))
            return null;

        try
        {
            using Icon? icon = Icon.ExtractAssociatedIcon(executablePath);
            return icon is null ? null : (Icon)icon.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> IsCdpReadyAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2.5));

        return await CdpInputService.IsPro32DebugEndpointAvailableAsync(timeout.Token);
    }

    private static async Task WaitForCdpAsync(CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + StartupTimeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await IsCdpReadyAsync(cancellationToken))
                return;

            await Task.Delay(250, cancellationToken);
        }

        throw new InvalidOperationException(
            "PRO32 Connect Dashboard был запущен с параметрами CDP, " +
            "но порт 127.0.0.1:9222 не стал доступен за 15 секунд. " +
            "Проверь, не занят ли порт 9222 другим приложением.");
    }

    private static void StartDashboard(string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                "PRO32 Connect Dashboard.exe не найден. Ожидаемый путь:" +
                Environment.NewLine + executablePath,
                executablePath);
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            Arguments = RemoteDebuggingArguments,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
            UseShellExecute = true
        };

        Process? process = Process.Start(startInfo);

        if (process is null)
        {
            throw new InvalidOperationException(
                "Не удалось запустить PRO32 Connect Dashboard с параметрами CDP.");
        }

        process.Dispose();
    }

    private static async Task StopDashboardProcessesAsync(
        IReadOnlyCollection<Process> processes,
        CancellationToken cancellationToken)
    {
        foreach (Process process in processes)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Процесс успел завершиться самостоятельно.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Один из дочерних Electron-процессов мог уже завершиться вместе
                // с главным процессом. Финальная проверка ниже определит результат.
            }
        }

        DateTime deadline = DateTime.UtcNow + StopTimeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<Process> remaining = GetDashboardProcesses();

            try
            {
                if (remaining.Count == 0)
                    return;
            }
            finally
            {
                DisposeProcesses(remaining);
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new InvalidOperationException(
            "Не удалось полностью завершить PRO32 Connect Dashboard перед перезапуском.");
    }

    private static List<Process> GetDashboardProcesses()
    {
        return Process.GetProcessesByName(DashboardProcessName).ToList();
    }

    private static string ResolveExecutablePath(IEnumerable<Process> processes)
    {
        foreach (Process process in processes)
        {
            try
            {
                string? fileName = process.MainModule?.FileName;

                if (!string.IsNullOrWhiteSpace(fileName) && File.Exists(fileName))
                    return fileName;
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        return ExecutablePath;
    }

    private static void DisposeProcesses(IEnumerable<Process> processes)
    {
        foreach (Process process in processes)
            process.Dispose();
    }

    internal readonly record struct EnsureResult(
        bool WasStarted,
        bool WasRestarted,
        string ExecutablePath);
}
