#if WINDOWS
using System.Runtime.InteropServices;

namespace LMP.UI.Features.Shell;

/// <summary>
/// Нативный менеджер системного трея для Windows.
/// 
/// <para><b>Архитектура:</b></para>
/// <list type="bullet">
///   <item>Нативная иконка через Shell_NotifyIcon V4 (tooltip, icon, hover events)</item>
///   <item>Lazy Low-Level Mouse Hook на <b>выделенном потоке</b> с собственным message pump:
///         устанавливается ТОЛЬКО при hover над иконкой,
///         снимается через 2 секунды после ухода курсора ИЛИ при активации главного окна</item>
///   <item>Shell_NotifyIconGetRect для определения позиции иконки (Windows 7+)</item>
///   <item>Кастомный Avalonia Popup вместо нативного Win32 контекстного меню</item>
/// </list>
/// 
/// <para><b>Почему Dedicated Hook Thread:</b>
/// WH_MOUSE_LL callback вызывается на потоке, который установил hook, через его message pump.
/// Если hook установлен на UI-потоке Avalonia, то во время layout/render/GC message pump
/// тормозит, и ВСЯ система получает mouse lag. Выделенный поток с легковесным
/// GetMessage/DispatchMessage loop не зависит от Avalonia — мышь всегда отзывчива.
/// См. MSDN LowLevelMouseProc: "it should run the hooks on a dedicated thread
/// that passes the work off to a worker thread and then immediately returns."</para>
/// 
/// <para><b>Почему Lazy Hook:</b> Даже на выделенном потоке, постоянный WH_MOUSE_LL hook
/// обрабатывает КАЖДОЕ mouse event системы. Lazy hook минимизирует время активности:
/// WM_MOUSEMOVE от Shell_NotifyIcon V4 → install hook → wheel scroll → 
/// uninstall через 2с неактивности ИЛИ при активации главного окна.</para>
/// 
/// <para><b>Громкость:</b> При скролле колесом мыши вызывается единый метод
/// <see cref="PlayerControlService.AdjustVolume"/>, а сохранение на диск
/// автоматически дебаунсится сервисом библиотеки без задержек в hook-потоке.</para>
/// 
/// <para><b>Toggle debounce:</b> NIN_SELECT и WM_LBUTTONDBLCLK могут приходить оба
/// при быстром клике. <see cref="TryInvokeToggle"/> использует cooldown
/// <see cref="MainWindow.ToggleCooldownMs"/> для предотвращения двойного toggle.</para>
/// 
/// <para><b>Tooltip формат:</b> <c>{AppName}: {TrackTitle} ({Volume}{VolumeEmoji})</c></para>
/// <para>Форматируется через <see cref="TrayTooltipHelper"/> (DRY).</para>
/// </summary>
internal sealed partial class TrayManager : IDisposable
{
    #region Constants

    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 1;

    // Mouse message IDs (V4: приходят как LOWORD(lParam))
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_DESTROY = 0x0002;

    /// <summary>
    /// V4 посылает WM_CONTEXTMENU вместе с WM_RBUTTONUP при правом клике.
    /// Обрабатываем ТОЛЬКО WM_CONTEXTMENU для ПКМ, чтобы избежать двойного срабатывания popup.
    /// </summary>
    private const int WM_CONTEXTMENU = 0x007B;

    // NIN_ notifications (V4: приходят как LOWORD(lParam))
    /// <summary>Курсор вошёл в область иконки (Shell_NotifyIcon V4).</summary>
    private const int NIN_POPUPOPEN = 0x0406;
    /// <summary>Курсор покинул область иконки (Shell_NotifyIcon V4).</summary>
    private const int NIN_POPUPCLOSE = 0x0407;

    /// <summary>
    /// V4: Пользователь кликнул (select) по иконке. Предпочтительнее WM_LBUTTONUP.
    /// См. MSDN: "for NOTIFYICON_VERSION_4 clients, NIN_SELECT is preferable".
    /// </summary>
    private const int NIN_SELECT = 0x0400;

    // Mouse Hook
    private const int WH_MOUSE_LL = 14;
    private const int HC_ACTION = 0;
    private const int WM_MOUSEWHEEL_MSG = 0x020A;

    // Shell_NotifyIcon
    private const int NIM_ADD = 0x00;
    private const int NIM_MODIFY = 0x01;
    private const int NIM_DELETE = 0x02;
    private const int NIM_SETVERSION = 0x04;

    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;

    /// <summary>
    /// Windows Vista+. Указывает Shell показывать стандартный tooltip при NOTIFYICON_VERSION_4.
    /// 
    /// <para><b>КРИТИЧНО:</b> Без этого флага при V4 tooltip подавляется (shell ожидает,
    /// что приложение рисует собственный popup по NIN_POPUPOPEN).
    /// Флаг действует только до следующего вызова Shell_NotifyIcon —
    /// поэтому ОБЯЗАТЕЛЬНО передавать при каждом NIM_MODIFY.</para>
    /// </summary>
    private const int NIF_SHOWTIP = 0x80;

    /// <summary>
    /// V4 нужен для получения NIN_POPUPOPEN/NIN_POPUPCLOSE/NIN_SELECT/WM_CONTEXTMENU.
    /// WM_MOUSEWHEEL перехватывается через lazy Mouse Hook на выделенном потоке.
    /// </summary>
    private const int NOTIFYICON_VERSION_4 = 4;

    /// <summary>Шаг изменения громкости при скролле колесиком.</summary>
    private const int VolumeStep = 2;

    /// <summary>Минимальный интервал обновления tooltip при hover (мс).</summary>
    private const int TooltipRefreshIntervalMs = 2000;

    /// <summary>
    /// Таймаут автоматического снятия mouse hook после последнего hover/wheel (мс).
    /// 2 секунды достаточно для комфортного скролла с паузами.
    /// </summary>
    private const int HookAutoUninstallMs = 2000;

    /// <summary>Время жизни кеша RECT иконки трея (мс).</summary>
    private const int IconRectCacheTtlMs = 5000;

    /// <summary>Custom message для управления hook из UI-потока → hook thread.</summary>
    private const int WM_APP_INSTALL_HOOK = WM_USER + 100;
    private const int WM_APP_UNINSTALL_HOOK = WM_USER + 101;
    private const int WM_APP_SHUTDOWN = WM_USER + 102;

    #endregion

    #region Win32 Imports — Shell & Window

#pragma warning disable SYSLIB1054
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);
#pragma warning restore SYSLIB1054

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial IntPtr DefWindowProcW(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandleW(string? lpModuleName);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    private static partial IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT lpPoint);

    /// <summary>
    /// PostThreadMessage — реальный DLL-экспорт: PostThreadMessageW.
    /// LibraryImport НЕ добавляет суффикс W автоматически — EntryPoint обязателен.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(uint dwThreadId, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    /// <summary>
    /// GetMessage возвращает int: -1 (ошибка), 0 (WM_QUIT), >0 (обычное сообщение).
    /// Маршаллинг как bool некорректен: -1 → true = бесконечный цикл при ошибке.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static partial int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    private static partial IntPtr DispatchMessageW(ref MSG lpMsg);

    /// <summary>
    /// PeekMessage — принудительно создаёт Win32 message queue на текущем потоке.
    /// Необходимо вызвать ДО Set() на ManualResetEvent, чтобы PostThreadMessage
    /// от UI-потока не упал с ERROR_INVALID_THREAD_ID.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    private const uint PM_NOREMOVE = 0x0000;

    #endregion

    #region Win32 Imports — Mouse Hook

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static partial IntPtr SetWindowsHookExW(int idHook, IntPtr lpfn, IntPtr hMod, uint dwThreadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(IntPtr hhk);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [LibraryImport("shell32.dll")]
    private static partial int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    #endregion

    #region Win32 Structs

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public int mouseData;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    #endregion

    #region Fields

    private readonly PlayerControlService _playerControl;
    private readonly Action? _onToggleWindow;
    private readonly Action? _onExit;
    private readonly Action? _onOpenQueue;
    private readonly Action<int>? _onVolumeChanged;

    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private WndProcDelegate? _wndProc;
    private bool _isVisible;
    private bool _disposed;
    private string _tooltip = "Lite Music Player";

    /// <summary>Кастомное Avalonia-окно контекстного меню трея. Создаётся лениво.</summary>
    private TrayPopupWindow? _popupWindow;

    // Dedicated Hook Thread

    /// <summary>
    /// Выделенный поток для WH_MOUSE_LL hook.
    /// Имеет собственный Win32 message loop (GetMessage/DispatchMessage),
    /// полностью независимый от Avalonia UI thread.
    /// </summary>
    private Thread? _hookThread;

    /// <summary>Thread ID выделенного потока — для PostThreadMessage.</summary>
    private volatile uint _hookThreadId;

    /// <summary>
    /// Сигнал готовности hook thread (message loop запущен).
    /// Install/Uninstall запросы через PostThreadMessage валидны только после Set().
    /// </summary>
    private readonly ManualResetEventSlim _hookThreadReady = new(false);

    /// <summary>Handle low-level mouse hook. Доступ ТОЛЬКО из hook thread.</summary>
    private IntPtr _mouseHookHandle;

    /// <summary>
    /// Делегат mouse hook callback — ОБЯЗАТЕЛЬНО хранить ссылку, иначе GC соберёт.
    /// </summary>
    private LowLevelMouseProc? _mouseHookDelegate;

    /// <summary>
    /// Timestamp последнего hover/wheel события (Environment.TickCount64).
    /// </summary>
    private long _lastHookActivityTime;

    /// <summary>Таймер автоснятия hook. Runs on UI thread.</summary>
    private Avalonia.Threading.DispatcherTimer? _hookTimeoutTimer;

    /// <summary>Флаг видимости popup — volatile для lock-free чтения из hook thread.</summary>
    private volatile bool _isPopupVisible;

    /// <summary>Флаг: hook сейчас установлен. Volatile для чтения из UI thread.</summary>
    private volatile bool _isHookInstalled;

    // Кеш RECT иконки
    private RECT _cachedIconRect;
    private long _iconRectCacheTime;
    private volatile bool _iconRectCacheValid;

    /// <summary>Timestamp последнего обновления tooltip при hover.</summary>
    private DateTime _lastTooltipRefresh = DateTime.MinValue;

    /// <summary>
    /// Timestamp последнего toggle окна (Environment.TickCount64).
    /// Используется для debounce NIN_SELECT / WM_LBUTTONDBLCLK.
    /// </summary>
    private long _lastToggleTime;

    private const string WindowClassName = "LMPTrayWindow";

    /// <summary>
    /// Возвращает актуальное состояние видимости главного окна.
    /// Func вместо bool — актуальное значение на момент открытия popup.
    /// </summary>
    private readonly Func<bool>? _isWindowVisible;

    #endregion

    #region Constructor

    /// <summary>
    /// Создаёт менеджер трея с нативной иконкой и кастомным Avalonia Popup.
    /// Mouse hook НЕ устанавливается сразу — только при hover над иконкой.
    /// Hook работает на выделенном потоке для устранения mouse lag.
    /// </summary>
    /// <param name="playerControl">Сервис управления плеером</param>
    /// <param name="onToggleWindow">Toggle show/hide главного окна (ЛКМ по иконке)</param>
    /// <param name="onExit">Закрыть приложение</param>
    /// <param name="onOpenQueue">Открыть очередь</param>
    /// <param name="onVolumeChanged">Callback при изменении громкости скроллом</param>
    /// <param name="isWindowVisible">
    /// Функция, возвращающая текущую видимость главного окна.
    /// Вызывается при каждом открытии popup для актуального состояния кнопки Show/Hide.
    /// </param>
    public TrayManager(
        PlayerControlService playerControl,
        Action? onToggleWindow = null,
        Action? onExit = null,
        Action? onOpenQueue = null,
        Action<int>? onVolumeChanged = null,
        Func<bool>? isWindowVisible = null)
    {
        _playerControl = playerControl;
        _onToggleWindow = onToggleWindow;
        _onExit = onExit;
        _onOpenQueue = onOpenQueue;
        _onVolumeChanged = onVolumeChanged;
        _isWindowVisible = isWindowVisible;

        _mouseHookDelegate = MouseHookCallback;

        CreateMessageWindow();
        StartHookThread();
    }

    #endregion

    #region Window Creation

    private void CreateMessageWindow()
    {
        _wndProc = WndProc;
        IntPtr hInstance = GetModuleHandleW(null);

        var wndClass = new WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = WindowClassName
        };

        RegisterClassW(ref wndClass);

        _hwnd = CreateWindowExW(
            0, WindowClassName, "LMP Tray",
            0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            Log.Error($"[TrayManager] CreateWindow failed, error={error}");
        }
    }

    #endregion

    #region Icon Management

    /// <summary>
    /// Показывает иконку в системном трее.
    /// Mouse hook НЕ устанавливается — будет установлен лениво при hover.
    /// </summary>
    public void Show(IntPtr? iconHandle = null)
    {
        if (_isVisible || _hwnd == IntPtr.Zero) return;

        if (iconHandle.HasValue && iconHandle.Value != IntPtr.Zero)
            _hIcon = iconHandle.Value;

        if (_hIcon == IntPtr.Zero)
            _hIcon = LoadDefaultIcon();

        var nid = CreateNotifyIconData();
        nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        nid.uCallbackMessage = WM_TRAYICON;
        nid.hIcon = _hIcon;
        nid.szTip = TruncateTooltip(_tooltip);

        bool result = Shell_NotifyIconW(NIM_ADD, ref nid);

        if (result)
        {
            nid.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIconW(NIM_SETVERSION, ref nid);

            _isVisible = true;
            Log.Debug("[TrayManager] Icon shown (hook will be installed on hover)");
        }
        else
        {
            int error = Marshal.GetLastWin32Error();
            Log.Warn($"[TrayManager] Shell_NotifyIcon ADD failed, error={error}");
        }
    }

    /// <summary>
    /// Скрывает иконку из системного трея и снимает hook если был установлен.
    /// </summary>
    public void Hide()
    {
        if (!_isVisible || _hwnd == IntPtr.Zero) return;

        RequestUninstallHook();

        var nid = CreateNotifyIconData();
        Shell_NotifyIconW(NIM_DELETE, ref nid);
        _isVisible = false;
        _iconRectCacheValid = false;

        Log.Debug("[TrayManager] Icon hidden");
    }

    /// <summary>
    /// Обновляет текст tooltip иконки трея.
    /// 
    /// <para><b>NIF_SHOWTIP:</b> Передаётся при КАЖДОМ NIM_MODIFY — иначе tooltip сбрасывается при V4.</para>
    /// 
    /// <para><b>forceRefresh:</b> При <c>true</c> сначала выставляет пустой szTip, затем реальный.
    /// Форсирует немедленное обновление tooltip window shell'ом.</para>
    /// </summary>
    public void SetTooltip(string text, bool forceRefresh = false)
    {
        _tooltip = text;

        if (!_isVisible || _hwnd == IntPtr.Zero) return;

        if (forceRefresh)
        {
            var clearNid = CreateNotifyIconData();
            clearNid.uFlags = NIF_TIP | NIF_SHOWTIP;
            clearNid.szTip = "";
            Shell_NotifyIconW(NIM_MODIFY, ref clearNid);
        }

        var nid = CreateNotifyIconData();
        nid.uFlags = NIF_TIP | NIF_SHOWTIP;
        nid.szTip = TruncateTooltip(text);
        Shell_NotifyIconW(NIM_MODIFY, ref nid);
    }

    /// <summary>
    /// Обновляет иконку в системном трее.
    /// NIF_SHOWTIP передаётся чтобы не сбросить отображение tooltip.
    /// </summary>
    public void SetIcon(IntPtr iconHandle)
    {
        _hIcon = iconHandle;

        if (!_isVisible || _hwnd == IntPtr.Zero) return;

        var nid = CreateNotifyIconData();
        nid.uFlags = NIF_ICON | NIF_SHOWTIP;
        nid.hIcon = iconHandle;

        Shell_NotifyIconW(NIM_MODIFY, ref nid);
    }

    /// <summary>
    /// Обновляет tooltip используя текущее состояние плеера.
    /// </summary>
    public void UpdateTooltipFromPlayerState()
    {
        int volume = _playerControl.GetCurrentVolume();
        SetTooltip(TrayTooltipHelper.Format(_playerControl.CurrentTrack, volume));
    }

    /// <summary>
    /// Обновляет tooltip с акцентом на громкости (для scroll events).
    /// Использует <c>forceRefresh: true</c> для мгновенного отображения.
    /// </summary>
    public void UpdateTooltipWithVolumeAccent()
    {
        int volume = _playerControl.GetCurrentVolume();
        SetTooltip(
            TrayTooltipHelper.FormatWithVolumeAccent(_playerControl.CurrentTrack, volume),
            forceRefresh: true);
    }

    /// <summary>
    /// Немедленно снимает hook и коммитит громкость.
    /// Вызывается из MainWindow при активации окна.
    /// </summary>
    public void ForceUninstallHook()
    {
        if (!_isHookInstalled) return;

        RequestUninstallHook();
        Log.Debug("[TrayManager] Hook force-uninstalled (window activated)");
    }

    private NOTIFYICONDATAW CreateNotifyIconData()
    {
        return new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            szTip = "",
            szInfo = "",
            szInfoTitle = ""
        };
    }

    private static IntPtr LoadDefaultIcon()
        => LoadIconW(IntPtr.Zero, 32512);

    private static string TruncateTooltip(string text)
        => text.Length > TrayTooltipHelper.MaxTooltipLength
            ? text[..TrayTooltipHelper.MaxTooltipLength]
            : text;

    #endregion

    #region Icon RECT Cache

    /// <summary>
    /// Обновляет кешированный RECT иконки трея через P/Invoke.
    /// </summary>
    private void RefreshIconRectCache()
    {
        var id = new NOTIFYICONIDENTIFIER
        {
            cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = _hwnd,
            uID = 1
        };

        int hr = Shell_NotifyIconGetRect(ref id, out RECT rect);

        if (hr == 0)
        {
            _cachedIconRect = rect;
            _iconRectCacheValid = true;
        }
        else
        {
            _iconRectCacheValid = false;
        }

        Volatile.Write(ref _iconRectCacheTime, Environment.TickCount64);
    }

    /// <summary>
    /// Проверяет позицию курсора относительно кешированного RECT.
    /// Обновляет кеш если TTL истёк.
    /// </summary>
    private bool IsCursorOverTrayIconCached(int cursorX, int cursorY)
    {
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _iconRectCacheTime) > IconRectCacheTtlMs)
            RefreshIconRectCache();

        if (!_iconRectCacheValid)
            return false;

        return cursorX >= _cachedIconRect.Left && cursorX <= _cachedIconRect.Right
            && cursorY >= _cachedIconRect.Top && cursorY <= _cachedIconRect.Bottom;
    }

    #endregion

    #region Dedicated Hook Thread

    /// <summary>
    /// Запускает выделенный фоновый поток с Win32 message loop для mouse hook.
    /// </summary>
    private void StartHookThread()
    {
        _hookThread = new Thread(HookThreadProc)
        {
            Name = "LMP.MouseHookThread",
            IsBackground = true
        };
        _hookThread.Start();

        _hookThreadReady.Wait(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Entry point выделенного hook thread.
    /// 
    /// <para><b>Порядок инициализации:</b>
    /// <list type="number">
    ///   <item>PeekMessage — форсирует создание Win32 message queue (до Set!)</item>
    ///   <item>_hookThreadReady.Set() — PostThreadMessage от UI-потока безопасен</item>
    ///   <item>GetMessage loop — крутится до WM_APP_SHUTDOWN или ошибки</item>
    /// </list></para>
    /// </summary>
    private void HookThreadProc()
    {
        _hookThreadId = GetCurrentThreadId();

        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
        _hookThreadReady.Set();

        int result;
        while ((result = GetMessageW(out MSG msg, IntPtr.Zero, 0, 0)) != 0)
        {
            if (result == -1)
            {
                int error = Marshal.GetLastWin32Error();
                Log.Warn($"[TrayManager] HookThread GetMessage error={error}, exiting");
                break;
            }

            switch (msg.message)
            {
                case WM_APP_INSTALL_HOOK:
                    InstallHookOnThread();
                    break;

                case WM_APP_UNINSTALL_HOOK:
                    UninstallHookOnThread();
                    break;

                case WM_APP_SHUTDOWN:
                    UninstallHookOnThread();
                    return;

                default:
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                    break;
            }
        }

        UninstallHookOnThread();
    }

    /// <summary>
    /// Устанавливает hook на текущем (hook) потоке.
    /// Вызывается ТОЛЬКО из HookThreadProc по WM_APP_INSTALL_HOOK.
    /// </summary>
    private void InstallHookOnThread()
    {
        if (_mouseHookHandle != IntPtr.Zero) return;
        if (_mouseHookDelegate == null) return;

        IntPtr funcPtr = Marshal.GetFunctionPointerForDelegate(_mouseHookDelegate);
        IntPtr hMod = GetModuleHandleW("user32");

        _mouseHookHandle = SetWindowsHookExW(WH_MOUSE_LL, funcPtr, hMod, 0);

        if (_mouseHookHandle == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            Log.Warn($"[TrayManager] Failed to install mouse hook, error={error}");
            return;
        }

        _isHookInstalled = true;
        RefreshIconRectCache();

        Log.Debug("[TrayManager] Mouse hook installed (lazy, on hover)");
    }

    /// <summary>
    /// Снимает hook на текущем (hook) потоке.
    /// Вызывается ТОЛЬКО из HookThreadProc.
    /// </summary>
    private void UninstallHookOnThread()
    {
        if (_mouseHookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHookHandle);
            _mouseHookHandle = IntPtr.Zero;
            _isHookInstalled = false;
            Log.Debug("[TrayManager] Mouse hook uninstalled");
        }
    }

    #endregion

    #region Lazy Mouse Hook — UI Thread Side

    /// <summary>
    /// Запрашивает установку hook через PostThreadMessage на hook thread.
    /// Вызывается из UI-потока (WndProc) при hover над иконкой.
    /// </summary>
    private void RequestInstallHook()
    {
        Volatile.Write(ref _lastHookActivityTime, Environment.TickCount64);

        if (_isHookInstalled) return;

        uint threadId = _hookThreadId;
        if (threadId == 0) return;

        PostThreadMessage(threadId, WM_APP_INSTALL_HOOK, IntPtr.Zero, IntPtr.Zero);
        StartHookTimeoutTimer();
    }

    /// <summary>
    /// Запрашивает снятие mouse hook в выделенном потоке.
    /// </summary>
    private void RequestUninstallHook()
    {
        StopHookTimeoutTimer();

        uint threadId = _hookThreadId;
        if (threadId != 0 && _isHookInstalled)
        {
            PostThreadMessage(threadId, WM_APP_UNINSTALL_HOOK, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>
    /// Запускает DispatcherTimer для проверки таймаута hook.
    /// </summary>
    private void StartHookTimeoutTimer()
    {
        if (_hookTimeoutTimer == null)
        {
            _hookTimeoutTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1000)
            };
            _hookTimeoutTimer.Tick += OnHookTimeoutTick;
        }

        if (!_hookTimeoutTimer.IsEnabled)
            _hookTimeoutTimer.Start();
    }

    private void StopHookTimeoutTimer()
    {
        _hookTimeoutTimer?.Stop();
    }

    /// <summary>
    /// Tick handler: проверяет прошло ли достаточно времени с последней активности.
    /// </summary>
    private void OnHookTimeoutTick(object? sender, EventArgs e)
    {
        if (!_isHookInstalled)
        {
            StopHookTimeoutTimer();
            return;
        }

        long elapsed = Environment.TickCount64 - Volatile.Read(ref _lastHookActivityTime);
        if (elapsed >= HookAutoUninstallMs)
        {
            RequestUninstallHook();
        }
    }

    #endregion

    #region Mouse Hook Callback (runs on Hook Thread)

    /// <summary>
    /// Callback low-level mouse hook.
    /// 
    /// <para><b>Поток:</b> Runs на выделенном hook thread (НЕ Avalonia UI thread).</para>
    /// <para><b>Фильтрация:</b> Обрабатывает ТОЛЬКО WM_MOUSEWHEEL.</para>
    /// <para><b>Zero-alloc:</b> Читает поля через Marshal.ReadInt32.</para>
    /// </summary>
    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < HC_ACTION || (int)wParam != WM_MOUSEWHEEL_MSG)
            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);

        if (!_isVisible || _isPopupVisible)
            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);

        int cursorX = Marshal.ReadInt32(lParam, 0);
        int cursorY = Marshal.ReadInt32(lParam, 4);

        if (!IsCursorOverTrayIconCached(cursorX, cursorY))
            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);

        // Wheel event над иконкой
        Volatile.Write(ref _lastHookActivityTime, Environment.TickCount64);

        int mouseData = Marshal.ReadInt32(lParam, 8);
        short wheelDelta = (short)(mouseData >> 16);
        int volumeDelta = wheelDelta > 0 ? VolumeStep : -VolumeStep;

        // Единый центр управления: изменяет звук мгновенно и запускает дебаунс-сохранение в БД
        int newVolume = _playerControl.AdjustVolume(volumeDelta);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _onVolumeChanged?.Invoke(newVolume);
        });

        return 1;
    }

    #endregion

    #region Window Procedure & Toggle Debounce

    /// <summary>
    /// Обработчик оконных сообщений от Shell_NotifyIcon V4.
    /// 
    /// <para><b>Toggle debounce:</b> NIN_SELECT и WM_LBUTTONDBLCLK оба вызывают
    /// <see cref="TryInvokeToggle"/> с cooldown <see cref="MainWindow.ToggleCooldownMs"/>.
    /// Это предотвращает двойное срабатывание при быстром клике.</para>
    /// </summary>
    private IntPtr WndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
    {
        if (uMsg == WM_TRAYICON)
        {
            int eventId = (int)(lParam.ToInt64() & 0xFFFF);

            switch (eventId)
            {
                case NIN_SELECT:
                case WM_LBUTTONDBLCLK:
                    TryInvokeToggle();
                    break;

                case WM_CONTEXTMENU:
                    ShowPopupMenu();
                    break;

                case WM_MOUSEMOVE:
                    RequestInstallHook();

                    var now = DateTime.UtcNow;
                    if ((now - _lastTooltipRefresh).TotalMilliseconds > TooltipRefreshIntervalMs)
                    {
                        _lastTooltipRefresh = now;
                        UpdateTooltipFromPlayerState();
                    }
                    break;

                case NIN_POPUPOPEN:
                    RequestInstallHook();
                    break;

                case NIN_POPUPCLOSE:
                    break;
            }

            return IntPtr.Zero;
        }

        if (uMsg == WM_DESTROY)
        {
            Hide();
            return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, uMsg, wParam, lParam);
    }

    /// <summary>
    /// Вызывает toggle окна с debounce-защитой.
    /// 
    /// <para><b>Почему нужен debounce:</b>
    /// Shell_NotifyIcon V4 при быстром клике посылает NIN_SELECT + WM_LBUTTONDBLCLK.
    /// Без debounce окно мгновенно скрывается и показывается — шторм lifecycle-событий
    /// (17 VM делают dispose/recreate subscriptions). Cooldown <see cref="MainWindow.ToggleCooldownMs"/>
    /// гарантирует максимум 1 toggle в 800мс.</para>
    /// 
    /// <para>При throttle просто игнорируем событие — tooltip при клике скрывается shell'ом,
    /// поэтому визуальный feedback через tooltip невозможен.</para>
    /// </summary>
    private void TryInvokeToggle()
    {
        long now = Environment.TickCount64;
        long elapsed = now - Volatile.Read(ref _lastToggleTime);

        if (elapsed < MainWindow.ToggleCooldownMs)
        {
            Log.Debug($"[TrayManager] Toggle throttled (elapsed={elapsed}ms < {MainWindow.ToggleCooldownMs}ms)");
            return;
        }

        Volatile.Write(ref _lastToggleTime, now);
        _onToggleWindow?.Invoke();
    }

    #endregion

    #region Popup Menu (Avalonia)

    /// <summary>
    /// Показывает кастомное Avalonia-окно контекстного меню.
    /// Передаёт актуальное состояние видимости окна для корректного текста Show/Hide.
    /// </summary>
    private void ShowPopupMenu()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_popupWindow == null)
                {
                    _popupWindow = new TrayPopupWindow();
                    _popupWindow.Initialize(
                        _playerControl,
                        onShowWindow: _onToggleWindow,
                        onExit: _onExit,
                        onOpenQueue: _onOpenQueue,
                        onCleanMemory: () => MemoryCleanupHelper.PerformCleanup(aggressive: true),
                        onVolumeChanged: vol => _onVolumeChanged?.Invoke(vol));

                    _popupWindow.PropertyChanged += (_, e) =>
                    {
                        if (e.Property == Avalonia.Visual.IsVisibleProperty)
                            _isPopupVisible = _popupWindow.IsVisible;
                    };
                }

                bool isVisible = _isWindowVisible?.Invoke() ?? false;
                _popupWindow.UpdateState(isVisible);

                GetCursorPos(out POINT pt);
                _popupWindow.ShowAt(pt.X, pt.Y);
            }
            catch (Exception ex)
            {
                Log.Error($"[TrayManager] Failed to show popup: {ex.Message}");
            }
        });
    }

    #endregion

    #region Dispose

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopHookTimeoutTimer();
        _hookTimeoutTimer = null;

        uint threadId = _hookThreadId;
        if (threadId != 0)
        {
            PostThreadMessage(threadId, WM_APP_SHUTDOWN, IntPtr.Zero, IntPtr.Zero);
            _hookThread?.Join(TimeSpan.FromSeconds(2));
        }
        _hookThreadReady.Dispose();

        Hide();

        try { _popupWindow?.Close(); }
        catch { /* ignore */ }
        _popupWindow = null;

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        _wndProc = null;
        _mouseHookDelegate = null;
        Log.Debug("[TrayManager] Disposed");
    }

    #endregion
}
#endif