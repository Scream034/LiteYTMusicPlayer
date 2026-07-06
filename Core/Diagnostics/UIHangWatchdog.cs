using System.Runtime.InteropServices;
using Avalonia.Threading;
using Microsoft.Diagnostics.NETCore.Client;

namespace LMP.Core.Diagnostics;

/// <summary>
/// Высокопроизводительный диагностический сторожевой таймер (Watchdog) для детекции зависаний UI-потока.
/// Работает на выделенном системном потоке с наивысшим приоритетом.
/// При фиксации зависания собирает метрики пула и генерирует диагностический дамп (.dmp)
/// через <see cref="DiagnosticsClient"/> (кроссплатформенно) с fallback на Win32 MiniDumpWriteDump.
/// </summary>
public sealed class UIHangWatchdog : IDisposable
{
    private readonly Thread _watchdogThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _dumpDirectory;
    private readonly int _hangThresholdMs;
    private volatile bool _disposed;

    /// <summary>
    /// Минимальный интервал между последовательными дампами во избежание генерации дубликатов.
    /// </summary>
    private const int CooldownAfterHangMs = 15_000;

    /// <summary>
    /// Интервал опроса UI-потока между проверками.
    /// </summary>
    private const int PollIntervalMs = 1_000;

    #region Win32 P/Invoke for MiniDumpWriteDump (Fallback)

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("dbghelp.dll", SetLastError = true, PreserveSig = true, EntryPoint = "MiniDumpWriteDump")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        IntPtr hFile,
        int dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);

    /// <summary>
    /// MiniDumpWithFullMemory (0x2) — единственный флаг, гарантирующий корректные
    /// managed call stacks для .NET Core / .NET 5+ при использовании Win32 API.
    /// Результирующий файл крупный (сотни MB), но содержит полный GC Heap и CLR метаданные.
    /// </summary>
    private const int MiniDumpWithFullMemory = 0x00000002;

    #endregion

    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="UIHangWatchdog"/>.
    /// </summary>
    /// <param name="dumpDirectory">Директория сохранения дампов. По умолчанию — папка логов LMP.</param>
    /// <param name="hangThresholdMs">Время ожидания отклика UI в миллисекундах. Рекомендуется ≥500 мс.</param>
    public UIHangWatchdog(string? dumpDirectory = null, int hangThresholdMs = 500)
    {
        _dumpDirectory = dumpDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LMP", "Logs");
        _hangThresholdMs = hangThresholdMs;

        Directory.CreateDirectory(_dumpDirectory);

        _watchdogThread = new Thread(WatchdogLoop)
        {
            Name = "LMP-UI-Hang-Watchdog",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
    }

    /// <summary>
    /// Запускает поток мониторинга.
    /// </summary>
    public void Start()
    {
        if (_disposed) return;
        _watchdogThread.Start();
        Log.Info($"[Watchdog] UI Hang Watchdog started. Threshold: {_hangThresholdMs}ms. Target dir: {_dumpDirectory}");
    }

    private void WatchdogLoop()
    {
        var token = _cts.Token;

        while (!token.IsCancellationRequested && !_disposed)
        {
            try
            {
                Thread.Sleep(PollIntervalMs);

                using var pingEvent = new ManualResetEventSlim(false);

                Dispatcher.UIThread.Post(() =>
                {
                    pingEvent.Set();
                }, DispatcherPriority.Send);

                if (!pingEvent.Wait(_hangThresholdMs, token))
                {
                    HandleUIHang();
                    Thread.Sleep(CooldownAfterHangMs);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"[Watchdog] Error in monitoring cycle: {ex.Message}");
            }
        }
    }

    private void HandleUIHang()
    {
        Log.Error($"[Watchdog] ⚠ DETECTED HANG IN AVALONIA UI THREAD! Freeze exceeded {_hangThresholdMs}ms.");

        LogThreadPoolMetrics();

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var dumpPath = Path.Combine(_dumpDirectory, $"LMP_UI_Hang_{timestamp}.dmp");

        // Приоритет: DiagnosticsClient (кроссплатформенный, корректные managed stacks)
        // Fallback: Win32 MiniDumpWriteDump с MiniDumpWithFullMemory (только Windows)
        if (TryWriteDumpViaDiagnosticsClient(dumpPath))
            return;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            TryWriteDumpViaWin32(dumpPath);
        }
        else
        {
            Log.Warn("[Watchdog] No dump mechanism available on this platform.");
        }
    }

    /// <summary>
    /// Создаёт дамп через <see cref="DiagnosticsClient"/>.
    /// Тип <see cref="DumpType.Full"/> гарантирует полные managed call stacks,
    /// GC Heap, информацию о потоках и модулях.
    /// </summary>
    /// <param name="dumpPath">Путь к файлу дампа.</param>
    /// <returns><c>true</c>, если дамп создан успешно.</returns>
    private static bool TryWriteDumpViaDiagnosticsClient(string dumpPath)
    {
        try
        {
            var pid = Environment.ProcessId;
            var client = new DiagnosticsClient(pid);

            Log.Info($"[Watchdog] Creating dump via DiagnosticsClient (DumpType.Full) → {dumpPath}");

            client.WriteDump(DumpType.Full, dumpPath, logDumpGeneration: false);

            Log.Error($"[Watchdog] ✓ DIAGNOSTIC DUMP CREATED SUCCESSFULLY: {dumpPath}");
            LogAnalysisInstructions();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"[Watchdog] DiagnosticsClient.WriteDump failed: {ex.Message}. Falling back to Win32 API.");
            return false;
        }
    }

    /// <summary>
    /// Создаёт дамп через Win32 MiniDumpWriteDump с флагом <c>MiniDumpWithFullMemory</c>.
    /// Это единственный Win32-флаг, гарантирующий корректную реконструкцию managed-стеков
    /// в .NET Core / .NET 5+ приложениях.
    /// </summary>
    /// <param name="dumpPath">Путь к файлу дампа.</param>
    private static void TryWriteDumpViaWin32(string dumpPath)
    {
        try
        {
            Log.Info($"[Watchdog] Creating dump via Win32 MiniDumpWriteDump (FullMemory) → {dumpPath}");

            using var fs = new FileStream(dumpPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var hProcess = GetCurrentProcess();
            var processId = GetCurrentProcessId();
            var hFile = fs.SafeFileHandle.DangerousGetHandle();

            bool success = MiniDumpWriteDump(
                hProcess,
                processId,
                hFile,
                MiniDumpWithFullMemory,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (success)
            {
                Log.Error($"[Watchdog] ✓ DIAGNOSTIC MINIDUMP CREATED SUCCESSFULLY: {dumpPath}");
                LogAnalysisInstructions();
            }
            else
            {
                var error = Marshal.GetLastWin32Error();
                Log.Error($"[Watchdog] ✗ MiniDumpWriteDump failed. Win32 Error: {error}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Watchdog] MiniDumpWriteDump failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Логирует метрики пула потоков для детекции ThreadPool Starvation.
    /// </summary>
    private static void LogThreadPoolMetrics()
    {
        ThreadPool.GetMinThreads(out int minWorker, out int minIo);
        ThreadPool.GetMaxThreads(out int maxWorker, out int maxIo);
        ThreadPool.GetAvailableThreads(out int availWorker, out int availIo);
        int activeWorkers = maxWorker - availWorker;

        Log.Warn(
            $"[Watchdog] ThreadPool Metrics — Active Workers: {activeWorkers}, " +
            $"Available: {availWorker}/{maxWorker}, Min: {minWorker}/{minIo}, " +
            $"IO Available: {availIo}/{maxIo}");
    }

    /// <summary>
    /// Логирует инструкции по анализу дампа.
    /// </summary>
    private static void LogAnalysisInstructions()
    {
        Log.Error("[Watchdog] 💡 ANALYSIS OPTIONS:");
        Log.Error("[Watchdog]   1. Visual Studio: Open .dmp → 'Debug with Managed Only' → Debug > Windows > Parallel Stacks");
        Log.Error("[Watchdog]   2. CLI: dotnet-dump analyze <file> → 'clrstack -all' → 'syncblk' → 'dumpasync'");
        Log.Error("[Watchdog]   3. WinDbg: Open .dmp → .loadby sos coreclr → !clrstack → !threads → !syncblk");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        if (_watchdogThread.IsAlive)
        {
            _watchdogThread.Join(500);
        }
        _cts.Dispose();
    }
}