using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Companion.Core;
using Companion.Services;
using Microsoft.Win32;
using System.Data;
using System.Windows.Media.Animation;
using Companion.Shell;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Clipboard = System.Windows.Clipboard;
using IDataObject = System.Windows.IDataObject;
using MessageBox = System.Windows.MessageBox;
using TextDataFormat = System.Windows.TextDataFormat;

namespace Companion.Shell.Windows;

public partial class IslandWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WmNcHitTest = 0x0084;
    private const int WmMouseHWheel = 0x020E;
    private const int WmClipboardUpdate = 0x031D;
    private const int HtTransparent = -1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private static readonly IntPtr HwndTopmost = new IntPtr(-1);
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint WineventOutOfContext = 0x0000;
    private const int QunsBusy = 2;
    private const int QunsRunningD3dFullScreen = 3;
    private const int QunsPresentationMode = 4;
    private const uint SpiSetDesktopWallpaper = 0x0014;
    private const uint SpifUpdateIniFile = 0x01;
    private const uint SpifSendChange = 0x02;

    private static readonly string[] TabNames =
    [
        "Controls", "Today", "Stats", "Clipboard", "Apps", "Wallpaper", "Power"
    ];

    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr windowHandle,
        int objectId, int childId, uint eventThreadId, uint eventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private void ApplyLoadout(SoulLoadout loadout)
    {
        _loadout = loadout;
        CollapsedSoulName.Text = loadout.SoulName;
        ExpandedSoulName.Text = loadout.SoulName;
        CollapsedSoul.VisualStyle = loadout.VisualStyle;
        CompactSoul.VisualStyle = loadout.VisualStyle;
        ExpandedSoul.VisualStyle = loadout.VisualStyle;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern IntPtr GetWindowLong32(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr windowHandle, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern IntPtr SetWindowLong32(IntPtr windowHandle, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr moduleHandle,
        WinEventDelegate callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWinEvent(IntPtr hookHandle);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, string value, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr processHandle, IntPtr minimumWorkingSetSize,
        IntPtr maximumWorkingSetSize);

    private readonly IslandAnimator _animator = new();
    private readonly DatabaseService _database = new();
    private readonly IslandActivityCoordinator _activityCoordinator = new();
    private readonly CountdownTimerService _countdownTimer;
    private readonly SystemControlService _systemControls = new();
    private readonly GlobalInputService _globalInput = new();
    private readonly MouseMonitorTracker _mouseMonitorTracker = new();
    private readonly DictationService _dictation;
    private readonly CircleToAskOverlay _circleOverlay;
    private readonly LoadoutIpcServer _ipcServer = new();
    private AutonomousScheduler? _scheduler;
    private MicCaptureService? _liveMicrophone;
    private AudioPlayerService? _liveAudioPlayer;
    private GeminiLiveService? _geminiLive;
    private CancellationTokenSource? _voiceSessionCancellation;
    private bool _voiceSessionStarting;
    private bool _liveVoiceFailureReported;
    private readonly SpringValue _widthSpring;
    private readonly SpringValue _heightSpring;
    private readonly SpringValue _cornerRadiusSpring;
    private readonly SpringValue _expandedOpacitySpring;
    private readonly SpringValue _tabIndicatorSpring;
    private int _previousTab;
    private bool _isReducedMotion;
    private readonly MediaService _mediaService = new();
    private readonly GatekeeperSecurityEngine _gatekeeper;
    private readonly ToolExecutionService _toolExecution;
    private TaskCompletionSource<bool>? _permissionTcs;
    private readonly System.Windows.Threading.DispatcherTimer _idleTrimTimer;
    private readonly System.Windows.Threading.DispatcherTimer _mediaProgressTimer;
    private readonly System.Windows.Threading.DispatcherTimer _clockTimer;
    private readonly System.Windows.Threading.DispatcherTimer _timerDisplayTimer;
    private readonly WinEventDelegate _winEventCallback;
    private IntPtr _windowHandle;
    private IntPtr _lastActiveExternalWindow;
    private IntPtr _foregroundHook;
    private int _selectedTab;
    private bool _loadingTodos;
    private bool _updatingVolume;
    private IReadOnlyList<AppShortcut> _appShortcuts = [];
    private IReadOnlyList<string> _wallpapers = [];
    private IReadOnlyList<ClipEntry> _clipboardEntries = [];
    private bool _suppressClipboardRecording;
    private long _activitySequence;
    private string? _displayedTransientActivityId;
    private string? _positionedMonitorDeviceName;
    private SoulLoadout _loadout = new();
    private string? _albumPaletteKey;
    private Color _albumPrimaryColor = Color.FromRgb(56, 189, 248);
    private Color _albumSecondaryColor = Color.FromRgb(129, 140, 248);

    public IslandWindow()
    {
        InitializeComponent();

        _activityCoordinator.CurrentActivityChanged += OnCurrentActivityChanged;
        _activityCoordinator.Publish("idle-clock", IslandActivityKind.IdleClock, "Clock", "", lifetime: null);
        _dictation = new DictationService(_database);
        _countdownTimer = new CountdownTimerService(_database);
        _countdownTimer.Completed += OnCountdownTimerCompleted;
        _circleOverlay = new CircleToAskOverlay();
        _gatekeeper = new GatekeeperSecurityEngine(PromptPermissionApprovalAsync, _database);
        _toolExecution = new ToolExecutionService(
            _database,
            _systemControls,
            _mediaService,
            LaunchAppByName,
            _gatekeeper);
        _widthSpring = _animator.CreateSpring(164);
        _heightSpring = _animator.CreateSpring(36);
        _cornerRadiusSpring = _animator.CreateSpring(18);
        _expandedOpacitySpring = _animator.CreateSpring(0.0, omega: 28.0, zeta: 0.82);
        _tabIndicatorSpring = _animator.CreateSpring(0.0, omega: 26.0, zeta: 0.85);
        _idleTrimTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(2)
        };
        _idleTrimTimer.Tick += OnIdleTrimTimerTick;
        _mediaProgressTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _mediaProgressTimer.Tick += OnMediaProgressTick;
        _clockTimer = new System.Windows.Threading.DispatcherTimer();
        _clockTimer.Tick += OnClockTick;
        _timerDisplayTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timerDisplayTimer.Tick += OnTimerDisplayTick;
        _animator.FrameTick += ApplySpringFrame;
        _winEventCallback = OnWinEvent;

        _mediaService.TrackChanged += OnMediaTrackChanged;
        _mediaService.PlaybackStateChanged += OnMediaPlaybackStateChanged;

        _globalInput.DictationStarted += OnDictationStarted;
        _globalInput.DictationReleased += OnDictationReleased;
        _globalInput.CircleToAskStarted += OnCircleToAskStarted;
        _globalInput.CircleToAskReleased += OnCircleToAskReleased;
        _globalInput.AssistantHotkeyPressed += OnAssistantHotkeyPressed;
        _globalInput.KillSwitchPressed += OnKillSwitchPressed;
        _globalInput.PermissionAllowPressed += () => OnPermissionAllowClick(this, new RoutedEventArgs());
        _globalInput.PermissionDenyPressed += () => OnPermissionDenyClick(this, new RoutedEventArgs());
        _mouseMonitorTracker.MonitorChanged += OnMouseMonitorChanged;
        _dictation.AudioLevelChanged += level => CompactSoul.AudioLevel = level;
        _ipcServer.LoadoutChanged += loadout => Dispatcher.BeginInvoke(() => ApplyLoadout(loadout));
        _database.ScheduledActionsChanged += () => Dispatcher.BeginInvoke(LoadScheduledActions);
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }

    private enum IslandState
    {
        Collapsed,
        NowBar,
        CompactLive,
        VoiceActive,
        PermissionPrompt,
        ExpandedDock
    }

    private static readonly IReadOnlyDictionary<IslandState, (double Width, double Height, double Radius)> StateSizes =
        new Dictionary<IslandState, (double Width, double Height, double Radius)>
        {
            [IslandState.Collapsed] = (164, 36, 18),
            [IslandState.NowBar] = (420, 40, 20),
            [IslandState.CompactLive] = (360, 56, 28),
            [IslandState.VoiceActive] = (440, 180, 28),
            [IslandState.PermissionPrompt] = (440, 180, 28),
            [IslandState.ExpandedDock] = (640, 390, 32)
        };

    private IslandState _state = IslandState.Collapsed;

    private void CheckReducedMotion()
    {
        _isReducedMotion = !SystemParameters.ClientAreaAnimation;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Window)
        {
            CheckReducedMotion();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _idleTrimTimer.Start();
        CheckReducedMotion();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        TabBar.SizeChanged += (s, ev) =>
        {
            if (TabBar.ActualWidth > 0 && TabIndicator is not null)
            {
                double tabWidth = TabBar.ActualWidth / 7.0;
                TabIndicator.Width = tabWidth;
                _tabIndicatorSpring.Reset(_selectedTab * tabWidth);
                if (TabIndicatorTransform is not null)
                {
                    TabIndicatorTransform.X = _selectedTab * tabWidth;
                }
            }
        };

        UpdateContentForState();
        ApplyLoadout(LoadoutStore.Load());
        ClockText.Text = DateTime.Now.ToString("h:mm");
        PositionAtTopCenter();
        LoadTodos();
        LoadStats();
        LoadApps();
        LoadWallpapers();
        LoadScheduledActions();
        LoadClipboardEntries();
        UpdateSelectedTab();
        LoadAudioControls();
        UpdateDarkModeButton();
        UpdateClockTimerState();
        _ = _mediaService.InitializeAsync();
        _countdownTimer.Restore();
        if (_countdownTimer.IsActive)
        {
            _activityCoordinator.Publish("timer-running", IslandActivityKind.TimerRunning, "Countdown", "Timer is running.");
        }

        UpdateTimerDisplayState();
        UpdatePersistentIslandState();
        LoadWifiInfo();
        LoadBluetoothInfo();
        _scheduler = new AutonomousScheduler(_database, ExecuteScheduledActionAsync);
        _scheduler.ActionCompleted += action => Dispatcher.BeginInvoke(() =>
        {
            LoadScheduledActions();
            ShowCompactMessage($"Scheduled: {action.Description}", Controls.SoulState.Acting, IslandActivityKind.ScheduledAction);
        });
        _scheduler.ActionFailed += (action, exception) => Dispatcher.BeginInvoke(() =>
        {
            LoadScheduledActions();
            ShowCompactMessage($"Scheduled action failed: {exception.Message}", Controls.SoulState.Error, IslandActivityKind.Error);
        });
        _scheduler.SchedulerFailed += exception => Dispatcher.BeginInvoke(() =>
            ShowCompactMessage($"Scheduler stopped: {exception.Message}", Controls.SoulState.Error));
        _scheduler.Start();
        TrimWorkingSet();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        if (!AddClipboardFormatListener(_windowHandle))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        IntPtr extendedStyle = GetWindowLong(_windowHandle, GwlExStyle);
        SetWindowLong(_windowHandle, GwlExStyle,
            new IntPtr(extendedStyle.ToInt64() | WsExToolWindow | WsExNoActivate));
        SetWindowPos(_windowHandle, HwndTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpFrameChanged);

        PositionAtTopCenter();
        HwndSource.FromHwnd(_windowHandle)?.AddHook(WindowProcedure);
        _foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground,
            IntPtr.Zero, _winEventCallback, 0, 0, WineventOutOfContext);
        IntPtr currentForeground = GetForegroundWindow();
        if (currentForeground != IntPtr.Zero && currentForeground != _windowHandle)
        {
            _lastActiveExternalWindow = currentForeground;
        }

        _globalInput.Start();
        try
        {
            _mouseMonitorTracker.Start();
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppPaths.Root, "shell.log"),
                    $"[{DateTime.UtcNow:u}] Mouse monitor tracking unavailable: {exception.Message}\n");
            }
            catch
            {
                // Monitor tracking is optional; keep the keyboard shortcuts available.
            }
        }

        _ipcServer.Start();
        UpdateFullScreenVisibility();
    }

    private IntPtr WindowProcedure(IntPtr windowHandle, int message, IntPtr wParam,
        IntPtr lParam, ref bool handled)
    {
        if (message == WmNcHitTest)
        {
            if (!IsPointInsideIsland(lParam))
            {
                handled = true;
                return new IntPtr(HtTransparent);
            }
        }
        else if (message == WmMouseHWheel)
        {
            int delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xffff));
            CycleTab(delta > 0 ? -1 : 1);
            handled = true;
            return IntPtr.Zero;
        }
        else if (message == WmClipboardUpdate)
        {
            Dispatcher.BeginInvoke(ProcessClipboardUpdate);
            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private bool IsPointInsideIsland(IntPtr lParam)
    {
        if (_windowHandle == IntPtr.Zero || !GetWindowRect(_windowHandle, out NativeRect rect))
        {
            return false;
        }

        long packed = lParam.ToInt64();
        int screenX = unchecked((short)(packed & 0xffff));
        int screenY = unchecked((short)((packed >> 16) & 0xffff));
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double localX = (screenX - rect.Left) / dpi.DpiScaleX;
        double localY = (screenY - rect.Top) / dpi.DpiScaleY;

        double islandWidth = IslandContainer.Width > 0 && !double.IsNaN(IslandContainer.Width)
            ? IslandContainer.Width
            : StateSizes[_state].Width;
        double islandHeight = IslandContainer.Height > 0 && !double.IsNaN(IslandContainer.Height)
            ? IslandContainer.Height
            : StateSizes[_state].Height;

        const double tolerance = 6.0;
        double left = (Width - islandWidth) / 2.0 - tolerance;
        double top = 0.0; // Anchored at top of screen without dead-zone
        double right = left + islandWidth + (tolerance * 2.0);
        double bottom = IslandContainer.Margin.Top + islandHeight + tolerance;
        return localX >= left && localX <= right && localY >= top && localY <= bottom;
    }

    private ScrollViewer? GetCurrentScroller() => _selectedTab switch
    {
        0 => TabScroller,
        1 => PageToday,
        3 => FindScrollViewer(ClipboardHistoryList),
        4 => FindScrollViewer(AppResults),
        5 => FindScrollViewer(WallpaperResults),
        _ => null
    };

    private static ScrollViewer? FindScrollViewer(DependencyObject? root)
    {
        if (root is null || root is not Visual)
        {
            return null;
        }

        if (root is ScrollViewer sv)
        {
            return sv;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var result = FindScrollViewer(child);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            CycleTab(e.Delta > 0 ? -1 : 1);
            e.Handled = true;
            return;
        }

        if (e.Delta < 0 && _state is IslandState.Collapsed or IslandState.NowBar or IslandState.CompactLive)
        {
            SetState(IslandState.ExpandedDock);
            e.Handled = true;
        }
        else if (e.Delta > 0 && _state == IslandState.ExpandedDock)
        {
            ScrollViewer? scroller = GetCurrentScroller();
            if (scroller is not null && scroller.VerticalOffset > 0.5)
            {
                scroller.ScrollToVerticalOffset(Math.Max(0, scroller.VerticalOffset - 96));
            }
            else
            {
                SetState(IslandState.Collapsed);
            }

            e.Handled = true;
        }
    }

    private void OnIslandMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source is not null)
        {
            if (source is Button)
            {
                return;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        if (_state is IslandState.Collapsed or IslandState.NowBar or IslandState.CompactLive)
        {
            SetState(IslandState.ExpandedDock);
            e.Handled = true;
        }
    }

    private void SetState(IslandState state)
    {
        if (state == IslandState.Collapsed && HasPersistentNowBarActivity)
        {
            state = IslandState.NowBar;
        }
        else if (state == IslandState.NowBar && !HasPersistentNowBarActivity)
        {
            state = IslandState.Collapsed;
        }

        _state = state;
        IslandContainer.Cursor = state is IslandState.Collapsed or IslandState.NowBar
            ? System.Windows.Input.Cursors.Hand
            : System.Windows.Input.Cursors.Arrow;
        _globalInput.SetPermissionPromptActive(state == IslandState.PermissionPrompt);
        if (state is IslandState.Collapsed or IslandState.NowBar)
        {
            _idleTrimTimer.Stop();
            if (state == IslandState.Collapsed)
            {
                _idleTrimTimer.Start();
            }

            _widthSpring.Omega = state == IslandState.NowBar ? 24.0 : 30.0;
            _widthSpring.Zeta = state == IslandState.NowBar ? 0.82 : 0.88;
            _heightSpring.Omega = state == IslandState.NowBar ? 24.0 : 30.0;
            _heightSpring.Zeta = state == IslandState.NowBar ? 0.82 : 0.88;
            _cornerRadiusSpring.Omega = state == IslandState.NowBar ? 24.0 : 30.0;
            _cornerRadiusSpring.Zeta = state == IslandState.NowBar ? 0.82 : 0.88;
        }
        else
        {
            _idleTrimTimer.Stop();
            // Expand Morph (Idle -> ExpandedDock / LiveActivity): Omega = 26.0, Zeta = 0.76 (organic overshoot)
            _widthSpring.Omega = 26.0;
            _widthSpring.Zeta = 0.76;
            _heightSpring.Omega = 26.0;
            _heightSpring.Zeta = 0.76;
            _cornerRadiusSpring.Omega = 26.0;
            _cornerRadiusSpring.Zeta = 0.76;
        }

        (double width, double height, double radius) = StateSizes[state];
        if (state == IslandState.NowBar)
        {
            bool hasMusic = _mediaService.CurrentTrack?.IsPlaying == true;
            width = hasMusic ? (_countdownTimer.IsActive ? 420 : 360) : 250;
        }
        if (_isReducedMotion)
        {
            _widthSpring.Reset(width);
            _heightSpring.Reset(height);
            _cornerRadiusSpring.Reset(radius);
            _expandedOpacitySpring.Reset(state == IslandState.ExpandedDock ? 1.0 : 0.0);
            UpdateContentForState();
            UpdateClockTimerState();
            UpdateTimerDisplayState();
            ApplySpringFrame();
        }
        else
        {
            _widthSpring.Target = width;
            _heightSpring.Target = height;
            _cornerRadiusSpring.Target = radius;
            UpdateContentForState();
            UpdateClockTimerState();
            UpdateTimerDisplayState();
            _animator.Wake();
        }

        if (state == IslandState.ExpandedDock)
        {
            Activate();
        }

        UpdateMediaProgressTimerState();
    }

    private void TriggerHorizontalNudge()
    {
        if (_isReducedMotion)
        {
            return;
        }

        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(250)
        };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0.0)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-6, KeyTime.FromPercent(0.2)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(6, KeyTime.FromPercent(0.4)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-4, KeyTime.FromPercent(0.6)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(3, KeyTime.FromPercent(0.8)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1.0)));
        IslandNudgeTransform.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void OnIdleTrimTimerTick(object? sender, EventArgs e)
    {
        _idleTrimTimer.Stop();
        TrimWorkingSet();
    }

    private void OnClockTick(object? sender, EventArgs e)
    {
        _clockTimer.Stop();
        ClockText.Text = DateTime.Now.ToString("h:mm");
        UpdateClockTimerState();
    }

    private void UpdateClockTimerState()
    {
        if (!IsLoaded || _state is not (IslandState.Collapsed or IslandState.NowBar))
        {
            _clockTimer.Stop();
            return;
        }

        ClockText.Text = DateTime.Now.ToString("h:mm");
        long ticksUntilNextMinute = TimeSpan.TicksPerMinute - (DateTime.Now.Ticks % TimeSpan.TicksPerMinute);
        _clockTimer.Interval = TimeSpan.FromTicks(ticksUntilNextMinute);
        if (!_clockTimer.IsEnabled)
        {
            _clockTimer.Start();
        }
    }

    private bool HasPersistentNowBarActivity =>
        _mediaService.CurrentTrack?.IsPlaying == true || _countdownTimer.IsActive;

    private void SyncMediaActivity(MediaTrackInfo? track)
    {
        if (track?.IsPlaying == true)
        {
            string detail = string.IsNullOrWhiteSpace(track.Artist)
                ? track.Title
                : $"{track.Title} · {track.Artist}";
            _activityCoordinator.Publish("media", IslandActivityKind.Media, "Now playing", detail);
        }
        else
        {
            _activityCoordinator.Clear("media");
        }
    }

    private void UpdatePersistentIslandState()
    {
        if (_state is not (IslandState.Collapsed or IslandState.NowBar))
        {
            return;
        }

        SetState(HasPersistentNowBarActivity ? IslandState.NowBar : IslandState.Collapsed);
    }

    private void UpdateNowBarContent()
    {
        MediaTrackInfo? track = _mediaService.CurrentTrack;
        bool hasMusic = track?.IsPlaying == true;
        bool hasTimer = _countdownTimer.IsActive;
        CollapsedSoulName.Visibility = hasMusic ? Visibility.Collapsed : Visibility.Visible;
        CollapsedNowPlayingText.Text = hasMusic ? track!.Title : string.Empty;
        CollapsedNowPlayingText.Visibility = hasMusic ? Visibility.Visible : Visibility.Collapsed;
        CollapsedTimerDivider.Visibility = hasTimer ? Visibility.Visible : Visibility.Collapsed;
        CollapsedTimerText.Visibility = hasTimer ? Visibility.Visible : Visibility.Collapsed;
        ClockDivider.Visibility = Visibility.Visible;
        CollapsedMusicPrevious.Visibility = hasMusic ? Visibility.Visible : Visibility.Collapsed;
        CollapsedMusicPlayPause.Visibility = hasMusic ? Visibility.Visible : Visibility.Collapsed;
        CollapsedMusicNext.Visibility = hasMusic ? Visibility.Visible : Visibility.Collapsed;
        CollapsedMusicPlayPause.Content = hasMusic ? "⏸" : "▶";
        CollapsedEqualizer.IsPlaying = hasMusic;
        CollapsedEqualizer.Visibility = hasMusic ? Visibility.Visible : Visibility.Collapsed;
        UpdateTimerDisplay();
    }

    private void UpdateTimerDisplayState()
    {
        bool timerVisible = _countdownTimer.IsActive &&
            (_state == IslandState.NowBar || (_state == IslandState.ExpandedDock && _selectedTab == 1));
        if (timerVisible && !_timerDisplayTimer.IsEnabled)
        {
            UpdateTimerDisplay();
            _timerDisplayTimer.Start();
        }
        else if (!timerVisible && _timerDisplayTimer.IsEnabled)
        {
            _timerDisplayTimer.Stop();
        }

        UpdateNowBarContent();
    }

    private void OnTimerDisplayTick(object? sender, EventArgs e) => UpdateTimerDisplay();

    private void UpdateTimerDisplay()
    {
        if (!_countdownTimer.IsActive)
        {
            TimerStatusText.Text = "No timer running";
            TimerCancelButton.IsEnabled = false;
            CollapsedTimerText.Text = string.Empty;
            return;
        }

        TimeSpan remaining = _countdownTimer.Remaining;
        string formatted = remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{(int)remaining.TotalMinutes}:{remaining.Seconds:00}";
        TimerStatusText.Text = $"Ends in {formatted}";
        TimerCancelButton.IsEnabled = true;
        CollapsedTimerText.Text = formatted;
    }

    private void OnCountdownTimerCompleted()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _activityCoordinator.Clear("timer-running");
            UpdateTimerDisplayState();
            ShowCompactMessage("Countdown complete", Controls.SoulState.Acting, IslandActivityKind.TimerComplete);
            SafeDiagnostics.WriteEvent("timer", "completion-shown");
        });
    }

    private void OnTimerStartClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TimerMinutesInput.Text, out int minutes) || minutes is < 1 or > 1440)
        {
            TimerStatusText.Text = "Enter 1 to 1440 minutes.";
            return;
        }

        _countdownTimer.Start(TimeSpan.FromMinutes(minutes));
        _activityCoordinator.Publish("timer-running", IslandActivityKind.TimerRunning, "Countdown", "Timer is running.");
        SafeDiagnostics.WriteMetric("timer", "duration-seconds", (long)TimeSpan.FromMinutes(minutes).TotalSeconds);
        UpdateTimerDisplayState();
        UpdatePersistentIslandState();
    }

    private void OnTimerCancelClick(object sender, RoutedEventArgs e)
    {
        _countdownTimer.Cancel();
        _activityCoordinator.Clear("timer-running");
        UpdateTimerDisplayState();
        UpdatePersistentIslandState();
    }

    private void TrimWorkingSet()
    {
        if (_state != IslandState.Collapsed)
        {
            return;
        }

        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        using Process process = Process.GetCurrentProcess();
        if (!SetProcessWorkingSetSize(process.Handle, new IntPtr(-1), new IntPtr(-1)))
        {
            ShowCompactMessage(
                $"Could not trim idle memory: {new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message}",
                Controls.SoulState.Error);
        }
    }

    private void UpdateContentForState()
    {
        CollapsedContent.Visibility = _state is IslandState.Collapsed or IslandState.NowBar
            ? Visibility.Visible : Visibility.Collapsed;
        CompactContent.Visibility = _state is IslandState.CompactLive or IslandState.VoiceActive
            ? Visibility.Visible : Visibility.Collapsed;
        VoiceStopButton.Visibility = _state == IslandState.VoiceActive &&
            (_geminiLive is not null || _voiceSessionStarting)
                ? Visibility.Visible
                : Visibility.Collapsed;
        UpdateVoiceControls();
        UpdateNowBarContent();
        PermissionPromptContent.Visibility = _state == IslandState.PermissionPrompt
            ? Visibility.Visible : Visibility.Collapsed;

        if (_state == IslandState.ExpandedDock)
        {
            ExpandedContent.Visibility = Visibility.Visible;
            _expandedOpacitySpring.Target = 1.0;
        }
        else
        {
            _expandedOpacitySpring.Target = 0.0;
            if (_expandedOpacitySpring.Current <= 0.02)
            {
                ExpandedContent.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ApplySpringFrame()
    {
        IslandContainer.Width = _widthSpring.Current;
        IslandContainer.Height = _heightSpring.Current;
        IslandContainer.CornerRadius = new CornerRadius(Math.Min(_cornerRadiusSpring.Current, _heightSpring.Current / 2));
        ExpandedContent.Opacity = _expandedOpacitySpring.Current;
        ExpandedSlideTransform.Y = (1.0 - _expandedOpacitySpring.Current) * 10.0;
        if (TabIndicatorTransform is not null)
        {
            TabIndicatorTransform.X = _tabIndicatorSpring.Current;
        }
        if (_state != IslandState.ExpandedDock && _expandedOpacitySpring.Current <= 0.02)
        {
            ExpandedContent.Visibility = Visibility.Collapsed;
        }
    }

    private void CycleTab(int direction)
    {
        _selectedTab = (_selectedTab + direction + TabNames.Length) % TabNames.Length;
        CurrentTabLabel.Text = TabNames[_selectedTab];
        UpdateSelectedTab();
        if (_state != IslandState.ExpandedDock)
        {
            SetState(IslandState.ExpandedDock);
        }
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out int index) &&
            index >= 0 && index < TabNames.Length)
        {
            _selectedTab = index;
            CurrentTabLabel.Text = TabNames[index];
            UpdateSelectedTab();
        }
    }

    private void UpdateSelectedTab()
    {
        int direction = _selectedTab >= _previousTab ? 1 : -1;
        _previousTab = _selectedTab;

        TabScroller.Visibility = _selectedTab == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageToday.Visibility = _selectedTab == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageStats.Visibility = _selectedTab == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageClipboard.Visibility = _selectedTab == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageApps.Visibility = _selectedTab == 4 ? Visibility.Visible : Visibility.Collapsed;
        PageWallpaper.Visibility = _selectedTab == 5 ? Visibility.Visible : Visibility.Collapsed;
        PagePower.Visibility = _selectedTab == 6 ? Visibility.Visible : Visibility.Collapsed;
        UpdateTimerDisplayState();

        if (!_isReducedMotion && TabContentSlideTransform is not null && TabContent is not null)
        {
            TabContentSlideTransform.X = direction * 4.0;
            var slideAnim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            TabContentSlideTransform.BeginAnimation(TranslateTransform.XProperty, slideAnim);

            TabContent.Opacity = 0.0;
            var fadeAnim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(120));
            TabContent.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
        }
        else if (TabContent is not null)
        {
            TabContent.Opacity = 1.0;
            if (TabContentSlideTransform is not null)
            {
                TabContentSlideTransform.X = 0;
            }
        }

        if (_selectedTab == 0)
        {
            LoadAudioControls();
            LoadWifiInfo();
            LoadBluetoothInfo();
        }
        else if (_selectedTab == 1)
        {
            LoadTodos();
            LoadScheduledActions();
        }
        else if (_selectedTab == 2)
        {
            LoadStats();
        }
        else if (_selectedTab == 3)
        {
            LoadClipboardEntries();
        }
        else if (_selectedTab == 4)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => AppSearch?.Focus());
        }
        else if (_selectedTab == 5)
        {
            LoadWallpapers();
        }

        UpdateMediaProgressTimerState();

        if (TabBar is not null && TabIndicator is not null)
        {
            double tabWidth = TabBar.ActualWidth > 0 ? TabBar.ActualWidth / 7.0 : 88.0;
            TabIndicator.Width = tabWidth;
            if (_isReducedMotion)
            {
                _tabIndicatorSpring.Reset(_selectedTab * tabWidth);
                if (TabIndicatorTransform is not null)
                {
                    TabIndicatorTransform.X = _selectedTab * tabWidth;
                }
            }
            else
            {
                _tabIndicatorSpring.Target = _selectedTab * tabWidth;
                _animator.Wake();
            }

            TabLabel0.Visibility = _selectedTab == 0 ? Visibility.Visible : Visibility.Collapsed;
            TabLabel1.Visibility = _selectedTab == 1 ? Visibility.Visible : Visibility.Collapsed;
            TabLabel2.Visibility = _selectedTab == 2 ? Visibility.Visible : Visibility.Collapsed;
            TabLabel3.Visibility = _selectedTab == 3 ? Visibility.Visible : Visibility.Collapsed;
            TabLabel4.Visibility = _selectedTab == 4 ? Visibility.Visible : Visibility.Collapsed;
            TabLabel5.Visibility = _selectedTab == 5 ? Visibility.Visible : Visibility.Collapsed;
            TabLabel6.Visibility = _selectedTab == 6 ? Visibility.Visible : Visibility.Collapsed;

            for (int i = 0; i < TabBar.Children.Count; i++)
            {
                if (TabBar.Children[i] is Button tabBtn)
                {
                    bool isSelected = i == _selectedTab;
                    tabBtn.Background = System.Windows.Media.Brushes.Transparent;
                    tabBtn.BorderBrush = System.Windows.Media.Brushes.Transparent;
                    tabBtn.BorderThickness = new Thickness(0);
                    tabBtn.Foreground = isSelected
                        ? (System.Windows.Media.Brush)FindResource("Text1Brush")
                        : (System.Windows.Media.Brush)FindResource("Text2Brush");
                    tabBtn.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
                }
            }
        }
    }

    private void OnCollapseClick(object sender, RoutedEventArgs e) => SetState(IslandState.Collapsed);

    private void OnStudioClick(object sender, RoutedEventArgs e)
    {
        LaunchStudio();
    }

    private static void LaunchStudio()
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "Companion.Studio.exe");
        if (!File.Exists(executable))
        {
            string siblingPublish = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Studio", "Companion.Studio.exe"));
            if (File.Exists(siblingPublish))
            {
                executable = siblingPublish;
            }
        }

        if (!File.Exists(executable))
        {
            string framework = Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
            string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
            executable = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..",
                "Companion.Studio", "bin", configuration, framework, "Companion.Studio.exe"));
        }

        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("Build Companion.Studio or publish it beside Companion.Shell to open settings.", executable);
        }

        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
    }

    private async void OnAssistantHotkeyPressed()
    {
        if (_geminiLive is not null || _voiceSessionStarting)
        {
            await StopLiveVoiceSessionAsync();
            ShowCompactMessage("Voice session ended", Controls.SoulState.Idle);
            return;
        }

        await StartLiveVoiceSessionAsync();
    }

    private async void OnTalkClick(object sender, RoutedEventArgs e)
    {
        if (_geminiLive is not null || _voiceSessionStarting)
        {
            await StopLiveVoiceSessionAsync();
            ShowCompactMessage("Voice session ended", Controls.SoulState.Idle);
            return;
        }

        await StartLiveVoiceSessionAsync();
    }

    private async void OnVoiceStopClick(object sender, RoutedEventArgs e)
    {
        await StopLiveVoiceSessionAsync();
        ShowCompactMessage("Voice session ended", Controls.SoulState.Idle);
    }

    private void UpdateVoiceControls()
    {
        TalkButton.Content = _voiceSessionStarting ? "Cancel" : _geminiLive is not null ? "End" : "Talk";
        TalkButton.ToolTip = _voiceSessionStarting
            ? "Cancel the live voice connection"
            : _geminiLive is not null
                ? "End the live voice conversation"
                : "Start a live voice conversation with Void";
        VoiceStopButton.Content = _voiceSessionStarting ? "Cancel" : "End";
        VoiceStopButton.Visibility = _state == IslandState.VoiceActive &&
            (_geminiLive is not null || _voiceSessionStarting)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    public void ToggleDock() =>
        SetState(_state == IslandState.ExpandedDock ? IslandState.Collapsed : IslandState.ExpandedDock);

    public void ShowDock() => SetState(IslandState.ExpandedDock);

    public void OpenStudioWindow() => LaunchStudio();

    private void OnDictationStarted()
    {
        try
        {
            _systemControls.DuckAudio();
            _dictation.Start();
            _activityCoordinator.Publish("dictation", IslandActivityKind.Dictation, "Dictation", "Listening for dictation.");
            CompactTitle.Text = "Dictation";
            CompactSubtitle.Text = "Listening for dictation...";
            CompactSoul.SoulState = Controls.SoulState.Listening;
            SetState(IslandState.CompactLive);
        }
        catch (Exception exception)
        {
            _activityCoordinator.Clear("dictation");
            ShowCompactMessage($"Dictation unavailable: {exception.Message}", Controls.SoulState.Error);
        }
    }

    private void OnCircleToAskStarted()
    {
        try
        {
            _systemControls.DuckAudio();
            CompactTitle.Text = "Circle to Ask";
            CompactSubtitle.Text = "Circle anything and ask out loud...";
            _activityCoordinator.Publish("circle-to-ask", IslandActivityKind.CircleToAsk, "Circle to Ask", "Select a screen region and ask out loud.");
            CompactSoul.SoulState = Controls.SoulState.Listening;
            SetState(IslandState.VoiceActive);
            _circleOverlay.BeginCapture();
        }
        catch (Exception exception)
        {
            _activityCoordinator.Clear("circle-to-ask");
            ShowCompactMessage($"Circle to Ask unavailable: {exception.Message}", Controls.SoulState.Error);
        }
    }

    private async void OnCircleToAskReleased()
    {
        _systemControls.UnduckAudio();
        if (!_circleOverlay.IsCapturing)
        {
            _activityCoordinator.Clear("circle-to-ask");
            return;
        }

        _activityCoordinator.Clear("circle-to-ask");
        CompactSubtitle.Text = "Analyzing the selected screen region...";
        CompactSoul.SoulState = Controls.SoulState.Thinking;
        try
        {
            (byte[] image, byte[] audio) = await _circleOverlay.EndCaptureAsync();
            try
            {
                await StartLiveVoiceSessionAsync(image, audio);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(image);
                CryptographicOperations.ZeroMemory(audio);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _activityCoordinator.Clear("circle-to-ask");
            ShowCompactMessage($"Circle to Ask failed: {exception.Message}", Controls.SoulState.Error);
        }
    }

    private async Task StartLiveVoiceSessionAsync(byte[]? initialImage = null, byte[]? initialAudio = null)
    {
        if (_geminiLive is not null || _voiceSessionStarting)
        {
            return;
        }

        string? apiKey = SecretStore.LoadApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ShowCompactMessage("Add a Gemini API key in Shape Void.", Controls.SoulState.Error);
            return;
        }

        _voiceSessionStarting = true;
        _liveVoiceFailureReported = false;
        _activityCoordinator.Publish(
            "voice-session",
            initialImage is null ? IslandActivityKind.Voice : IslandActivityKind.CircleToAsk,
            initialImage is null ? "Live conversation" : "Circle to Ask",
            "Listening to the user.");
        UpdateVoiceControls();
        var cancellation = new CancellationTokenSource();
        _voiceSessionCancellation = cancellation;
        _liveAudioPlayer = new AudioPlayerService();
        _geminiLive = new GeminiLiveService(
            _liveAudioPlayer,
            _toolExecution.Registry,
            ExecuteToolCallAsync,
            _toolExecution.TrackUntrustedContext);
        _liveMicrophone = new MicCaptureService();
        GeminiLiveService liveSession = _geminiLive;
        MicCaptureService microphone = _liveMicrophone;
        AudioPlayerService audioPlayer = _liveAudioPlayer;

        microphone.PcmChunkAvailable += liveSession.SendAudioChunk;
        microphone.AudioStreamEnded += liveSession.EndAudioStream;
        microphone.AudioLevelChanged += level => Dispatcher.BeginInvoke(() => CompactSoul.AudioLevel = level);
        liveSession.InputTranscriptReceived += transcript => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_geminiLive, liveSession))
            {
                return;
            }

            CompactTitle.Text = "You said";
            CompactSubtitle.Text = transcript;
        });
        liveSession.OutputTranscriptReceived += transcript => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_geminiLive, liveSession))
            {
                return;
            }

            CompactTitle.Text = string.IsNullOrWhiteSpace(_loadout.SoulName) ? "Void" : _loadout.SoulName;
            CompactSubtitle.Text = transcript;
        });
        liveSession.SpeakingChanged += speaking => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_geminiLive, liveSession))
            {
                return;
            }

            CompactSoul.SoulState = speaking ? Controls.SoulState.Speaking : Controls.SoulState.Listening;
        });
        liveSession.Interrupted += () => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_geminiLive, liveSession))
            {
                return;
            }

            CompactSoul.SoulState = Controls.SoulState.Listening;
            CompactSubtitle.Text = "Listening...";
        });
        liveSession.ConnectionFailed += exception => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_geminiLive, liveSession))
            {
                return;
            }

            _liveVoiceFailureReported = true;
            ShowCompactMessage($"Voice connection failed: {exception.Message}", Controls.SoulState.Error);
        });
        liveSession.SessionEnded += () => OnLiveSessionEnded(liveSession);

        CompactTitle.Text = "Void";
        CompactSubtitle.Text = "Connecting to Gemini Live...";
        CompactSoul.SoulState = Controls.SoulState.Thinking;
        SetState(IslandState.VoiceActive);
        try
        {
            await liveSession.ConnectAsync(apiKey, _loadout, _database.GetAllMemories().Take(5).ToArray(), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (initialImage is not null)
            {
                await liveSession.SendScreenQuestionAsync(initialImage, initialAudio ?? [], cancellation.Token);
            }

            microphone.StartRecording(retainAudio: false, streamContinuously: true);
            CompactTitle.Text = "Live conversation";
            CompactSubtitle.Text = audioPlayer.IsOutputAvailable
                ? "Listening… Say something or click End"
                : "Listening… speaker output is unavailable on this device";
            CompactSoul.SoulState = Controls.SoulState.Listening;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await StopLiveVoiceSessionAsync();
        }
        catch (Exception exception)
        {
            await StopLiveVoiceSessionAsync();
            ShowCompactMessage($"Could not start voice chat: {exception.Message}", Controls.SoulState.Error);
        }
        finally
        {
            _voiceSessionStarting = false;
            UpdateVoiceControls();
            if (ReferenceEquals(_voiceSessionCancellation, cancellation))
            {
                _voiceSessionCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private void OnLiveSessionEnded(GeminiLiveService endedSession)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (!ReferenceEquals(_geminiLive, endedSession))
            {
                return;
            }

            bool wasStarting = _voiceSessionStarting;
            bool failureWasReported = _liveVoiceFailureReported;
            await StopLiveVoiceSessionAsync();
            _liveVoiceFailureReported = false;
            if (!wasStarting && !failureWasReported)
            {
                ShowCompactMessage("Voice session ended", Controls.SoulState.Idle);
            }
        }));
    }

    private async Task StopLiveVoiceSessionAsync()
    {
        _voiceSessionCancellation?.Cancel();
        _activityCoordinator.Clear("voice-session");
        MicCaptureService? microphone = _liveMicrophone;
        GeminiLiveService? liveSession = _geminiLive;
        AudioPlayerService? audioPlayer = _liveAudioPlayer;
        _liveMicrophone = null;
        _geminiLive = null;
        _liveAudioPlayer = null;

        if (microphone is not null)
        {
            if (microphone.IsRecording)
            {
                await microphone.CancelRecordingAsync();
            }

            microphone.Dispose();
        }

        if (liveSession is not null)
        {
            await liveSession.DisposeAsync();
        }

        audioPlayer?.Dispose();
        CompactSoul.SoulState = Controls.SoulState.Idle;
        UpdateVoiceControls();
    }

    private async void OnDictationReleased()
    {
        _systemControls.UnduckAudio();
        if (!_dictation.IsRecording)
        {
            _activityCoordinator.Clear("dictation");
            return;
        }

        CompactTitle.Text = "Dictation";
        CompactSubtitle.Text = "Cleaning and typing...";
        CompactSoul.SoulState = Controls.SoulState.Thinking;
        try
        {
            string text = await _dictation.StopAndTypeAsync();
            int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            LoadStats();
            CompactTitle.Text = "Clean Dictation";
            CompactSubtitle.Text = $"Typed {words} clean words";
            CompactCheckmarkBadge.Visibility = Visibility.Visible;
            CompactSoul.Visibility = Visibility.Collapsed;
            SetState(IslandState.CompactLive);
            StartCollapseTimer(TimeSpan.FromMilliseconds(1200), IslandState.CompactLive, () =>
            {
                CompactCheckmarkBadge.Visibility = Visibility.Collapsed;
                CompactSoul.Visibility = Visibility.Visible;
            });
        }
        catch (OperationCanceledException)
        {
            SetState(IslandState.Collapsed);
        }
        catch (Exception exception)
        {
            ShowCompactMessage($"Dictation failed: {exception.Message}", Controls.SoulState.Error);
        }
        finally
        {
            _activityCoordinator.Clear("dictation");
        }
    }

    private async void OnKillSwitchPressed()
    {
        try
        {
            if (_voiceSessionStarting)
            {
                _voiceSessionCancellation?.Cancel();
            }
            else
            {
                await StopLiveVoiceSessionAsync();
            }

            await _circleOverlay.CancelCaptureAsync();
            await _dictation.CancelAsync();
            _activityCoordinator.Clear("circle-to-ask");
            _activityCoordinator.Clear("dictation");
            SetState(IslandState.Collapsed);
        }
        catch (Exception exception)
        {
            ShowCompactMessage($"Could not stop dictation: {exception.Message}", Controls.SoulState.Error);
        }
    }

    private void ShowCompactMessage(
        string message,
        Controls.SoulState soulState,
        IslandActivityKind? activityKind = null)
    {
        IslandActivityKind kind = activityKind ?? (soulState == Controls.SoulState.Error
            ? IslandActivityKind.Error
            : IslandActivityKind.Notification);
        TimeSpan lifetime = kind == IslandActivityKind.Error
            ? TimeSpan.FromSeconds(4)
            : TimeSpan.FromMilliseconds(1500);
        string id = $"compact-message-{Interlocked.Increment(ref _activitySequence)}";
        _activityCoordinator.Publish(id, kind, "Void", message, lifetime);
    }

    private void OnCurrentActivityChanged(IslandActivity? activity)
    {
        Dispatcher.BeginInvoke(new Action(() => ApplyCurrentActivity(_activityCoordinator.Current)));
    }

    private void ApplyCurrentActivity(IslandActivity? activity)
    {
        if (activity is null)
        {
            LeaveTransientActivity();
            return;
        }

        if (activity.Kind is IslandActivityKind.Gatekeeper or IslandActivityKind.Voice or
            IslandActivityKind.CircleToAsk or IslandActivityKind.Dictation)
        {
            if (activity.Kind is IslandActivityKind.Voice or IslandActivityKind.CircleToAsk &&
                _state == IslandState.CompactLive && (_geminiLive is not null || _voiceSessionStarting))
            {
                SetState(IslandState.VoiceActive);
            }

            if (activity.Kind == IslandActivityKind.Dictation && _state == IslandState.CompactLive && _dictation.IsRecording)
            {
                _displayedTransientActivityId = null;
            }

            return;
        }

        if (!activity.Id.StartsWith("compact-message-", StringComparison.Ordinal))
        {
            LeaveTransientActivity();
            return;
        }

        if (activity.ExpiresAtUtc is DateTimeOffset expiry && expiry <= DateTimeOffset.UtcNow)
        {
            LeaveTransientActivity();
            return;
        }

        _displayedTransientActivityId = activity.Id;
        CompactTitle.Text = activity.Title;
        CompactSubtitle.Text = activity.Detail;
        CompactSoul.SoulState = activity.Kind == IslandActivityKind.Error
            ? Controls.SoulState.Error
            : activity.Kind is IslandActivityKind.ScheduledAction or IslandActivityKind.TimerComplete or IslandActivityKind.Clipboard
                ? Controls.SoulState.Acting
                : Controls.SoulState.Idle;
        CompactCheckmarkBadge.Visibility = Visibility.Collapsed;
        CompactSoul.Visibility = Visibility.Visible;
        if (activity.Kind == IslandActivityKind.Error)
        {
            TriggerHorizontalNudge();
        }

        SetState(IslandState.CompactLive);
    }

    private void LeaveTransientActivity()
    {
        if (_displayedTransientActivityId is null)
        {
            return;
        }

        _displayedTransientActivityId = null;
        if (_state == IslandState.CompactLive)
        {
            CompactSoul.SoulState = Controls.SoulState.Idle;
            SetState(IslandState.Collapsed);
        }
    }

    private void StartCollapseTimer(TimeSpan interval, IslandState expectedState, Action? onCollapse = null)
    {
        var collapseTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = interval
        };
        collapseTimer.Tick += (_, _) =>
        {
            collapseTimer.Stop();
            try
            {
                onCollapse?.Invoke();
            }
            catch
            {
            }

            if (_state == expectedState)
            {
                CompactSoul.SoulState = Controls.SoulState.Idle;
                SetState(IslandState.Collapsed);
            }
        };
        collapseTimer.Start();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string uri })
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                ShowCompactMessage($"Could not open settings: {exception.Message}", Controls.SoulState.Error);
            }
        }
    }

    private void UpdateDarkModeButton()
    {
        bool enabled = _systemControls.IsDarkModeEnabled;
        DarkModeButton.Tag = enabled;
        DarkModeButton.Content = enabled ? "Dark mode · On" : "Dark mode · Off";
        DarkModeButton.Background = enabled
            ? (System.Windows.Media.Brush)FindResource("AccentGlassBrush")
            : (System.Windows.Media.Brush)FindResource("GlassCardBrush");
        DarkModeButton.BorderBrush = enabled
            ? (System.Windows.Media.Brush)FindResource("AccentBorderBrush")
            : (System.Windows.Media.Brush)FindResource("GlassCardBorderBrush");
    }

    private void OnDarkModeToggleClick(object sender, RoutedEventArgs e)
    {
        bool enabled = DarkModeButton.Tag is not true;
        _systemControls.SetDarkMode(enabled);
        UpdateDarkModeButton();
    }

    private void LoadAudioControls()
    {
        try
        {
            (string deviceName, int volumePercent) = _systemControls.GetDefaultAudioOutput();
            AudioDeviceLabel.Text = deviceName;
            _updatingVolume = true;
            MasterVolumeSlider.Value = volumePercent;
            VolumeValueText.Text = $"{volumePercent}%";
            _updatingVolume = false;
        }
        catch (Exception exception)
        {
            _updatingVolume = true;
            MasterVolumeSlider.IsEnabled = false;
            AudioDeviceLabel.Text = $"Audio unavailable: {exception.Message}";
            VolumeValueText.Text = string.Empty;
            _updatingVolume = false;
        }
    }

    private void OnMasterVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingVolume || !IsLoaded)
        {
            return;
        }

        int percent = (int)Math.Round(e.NewValue);
        _systemControls.SetMasterVolumePercent(percent);
        VolumeValueText.Text = $"{percent}%";
    }

    private void OnAddTodoClick(object sender, RoutedEventArgs e)
    {
        string title = TodoInput.Text.Trim();
        if (title.Length == 0)
        {
            return;
        }

        _database.AddTodo(title);
        TodoInput.Clear();
        LoadTodos();
    }

    private void OnScheduleAppClick(object sender, RoutedEventArgs e)
    {
        string appName = ScheduleAppInput.Text.Trim();
        if (appName.Length == 0)
        {
            ScheduledActionStatus.Text = "Enter the exact Start Menu app name to launch.";
            return;
        }

        if (!int.TryParse(ScheduleMinutesInput.Text, out int minutes) || minutes is < 1 or > 10080)
        {
            ScheduledActionStatus.Text = "Choose a delay between 1 minute and 7 days.";
            return;
        }

        string argsJson = JsonSerializer.Serialize(new { app_name = appName });
        _database.AddScheduledAction(
            $"Open {appName}",
            "open_app",
            argsJson,
            DateTime.UtcNow.AddMinutes(minutes));
        ScheduleAppInput.Clear();
        ScheduledActionStatus.Text = $"Scheduled to run in {minutes} minute(s).";
        LoadScheduledActions();
    }

    private void LoadScheduledActions()
    {
        ScheduledList.ItemsSource = _database.GetUpcomingActions()
            .Select(action =>
                $"{action.TriggerUtc.ToLocalTime():MMM d · h:mm tt} — {action.Description}")
            .ToArray();
        RoutineList.ItemsSource = _database.GetRoutines();
    }

    private void OnRoutineToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long id } && _database.GetRoutine(id) is Routine routine)
        {
            _database.SetRoutineEnabled(id, !routine.IsEnabled);
            LoadScheduledActions();
        }
    }

    private void OnRoutineRunNowClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long id } && _database.GetRoutine(id) is Routine routine)
        {
            if (!routine.IsEnabled)
            {
                ScheduledActionStatus.Text = $"'{routine.Name}' is paused. Turn it on before running it.";
                return;
            }

            _database.ScheduleRoutineNow(id);
            ScheduledActionStatus.Text = "Routine queued to run now.";
            LoadScheduledActions();
        }
    }

    private void OnRoutineDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long id })
        {
            _database.DeleteRoutine(id);
            LoadScheduledActions();
        }
    }

    private async Task ExecuteScheduledActionAsync(ScheduledAction action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using JsonDocument arguments = JsonDocument.Parse(action.ArgsJson);
        if (string.Equals(action.ToolName, "Routine.Run", StringComparison.Ordinal))
        {
            if (!arguments.RootElement.TryGetProperty("routine_id", out JsonElement routineIdElement) ||
                !routineIdElement.TryGetInt64(out long routineId))
            {
                throw new InvalidDataException("Scheduled routine is missing its routine id.");
            }

            Routine routine = _database.GetRoutine(routineId)
                ?? throw new InvalidOperationException($"Routine {routineId} no longer exists.");
            if (!routine.IsEnabled)
            {
                throw new InvalidOperationException($"Routine '{routine.Name}' is paused.");
            }

            using JsonDocument steps = JsonDocument.Parse(routine.StepsJson);
            foreach (JsonElement step in steps.RootElement.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string toolName = step.GetProperty("tool_name").GetString()
                    ?? throw new InvalidDataException("Routine step has no tool name.");
                await _toolExecution.ExecuteAsync(
                    toolName,
                    step.GetProperty("args"),
                    $"routine-{routine.Id}",
                    cancellationToken);
            }

            return;
        }

        await _toolExecution.ExecuteAsync(
            action.ToolName,
            arguments.RootElement,
            $"scheduled-{action.Id}",
            cancellationToken);
    }

    private async Task<string> ExecuteToolCallAsync(
        string name,
        System.Text.Json.JsonElement arguments,
        string sourceMessageId,
        CancellationToken cancellationToken)
    {
        string result = await _toolExecution.ExecuteAsync(name, arguments, sourceMessageId, cancellationToken);
        await Dispatcher.InvokeAsync(() =>
        {
            if (name.Equals("manage_todo", StringComparison.OrdinalIgnoreCase))
            {
                LoadTodos();
            }
            else if (name.Equals("schedule_action", StringComparison.OrdinalIgnoreCase))
            {
                LoadScheduledActions();
            }
            else if (name.Equals("remember_fact", StringComparison.OrdinalIgnoreCase))
            {
                CompactSubtitle.Text = "Memory saved";
            }

            LoadStats();
        });
        return result;
    }

    private void LaunchAppByName(string requestedName)
    {
        AppShortcut? exact = _appShortcuts.FirstOrDefault(app =>
            string.Equals(app.Name, requestedName, StringComparison.CurrentCultureIgnoreCase));
        AppShortcut? selected = exact;
        if (selected is null)
        {
            var matches = _appShortcuts
                .Select(app => (App: app, Score: SubsequenceScore(requestedName, app.Name)))
                .Where(match => match.Score >= 0)
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.App.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(2)
                .ToArray();
            if (matches.Length == 0)
            {
                throw new InvalidOperationException($"No Start Menu app matching '{requestedName}' was found.");
            }

            if (matches.Length > 1 && matches[0].Score == matches[1].Score)
            {
                throw new InvalidOperationException(
                    $"'{requestedName}' is ambiguous. Matches: {matches[0].App.Name}, {matches[1].App.Name}.");
            }

            selected = matches[0].App;
        }

        Process.Start(new ProcessStartInfo(selected.Path) { UseShellExecute = true });
    }

    private void LoadTodos()
    {
        _loadingTodos = true;
        IReadOnlyList<TodoItem> todos = _database.GetTodos();
        TodoList.ItemsSource = todos;
        int completed = todos.Count(todo => todo.IsCompleted);
        TodoCounter.Text = $"{completed}/{todos.Count} completed";
        _loadingTodos = false;
    }

    private void OnTodoCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingTodos || sender is not CheckBox { Tag: long id })
        {
            return;
        }

        if (_database.ToggleTodo(id))
        {
            LoadTodos();
        }
    }

    private void LoadStats()
    {
        DailyStats stats = _database.GetTodayStats();
        DictatedStat.Text = stats.DictatedWords.ToString();
        VoiceStat.Text = stats.VoiceCommands.ToString();
        ToolsStat.Text = stats.ToolsExecuted.ToString();
        double minutesSaved = Math.Max(0, (stats.DictatedWords / 40.0) - (stats.DictatedWords / 150.0));
        TimeSavedStat.Text = $"{minutesSaved:0.#} min";
    }

    private void LoadApps()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] startMenuDirectories =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        ];
        foreach (string root in startMenuDirectories.Where(Directory.Exists))
        {
            foreach (string path in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
            {
                paths.Add(path);
            }
        }

        _appShortcuts = paths
            .Select(path => new AppShortcut(Path.GetFileNameWithoutExtension(path), path))
            .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        UpdateAppResults();
    }

    private void OnAppSearchChanged(object sender, TextChangedEventArgs e) => UpdateAppResults();

    private void OnAppSearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        AppShortcut? app = AppResults.SelectedItem as AppShortcut ??
            AppResults.Items.Cast<AppShortcut>().FirstOrDefault();
        if (app is not null)
        {
            LaunchAppShortcut(app);
        }

        e.Handled = true;
    }

    private void UpdateAppResults()
    {
        if (AppResults is null)
        {
            return;
        }

        string query = AppSearch.Text.Trim();
        var virtualShortcuts = new List<AppShortcut>();

        if (query.StartsWith(">") && query.Length > 1)
        {
            string cmd = query[1..].Trim();
            virtualShortcuts.Add(new AppShortcut($"⚡ Run: {cmd}", "cmd:" + cmd));
        }
        else if (query.Length >= 2 && (query.Any(c => c is '+' or '-' or '*' or '/' or '%' or '^') || query.All(char.IsDigit)))
        {
            try
            {
                using var table = new DataTable();
                var computeResult = table.Compute(query, null);
                if (computeResult is not null && !string.IsNullOrEmpty(computeResult.ToString()))
                {
                    virtualShortcuts.Add(new AppShortcut($"= {computeResult}  (Click to Copy)", "math:" + computeResult));
                }
            }
            catch
            {
            }
        }

        AppShortcut[] matches = _appShortcuts
            .Select(app => (App: app, Score: SubsequenceScore(query, app.Name)))
            .Where(result => result.Score >= 0)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.App.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(30)
            .Select(result => result.App)
            .ToArray();

        AppResults.ItemsSource = virtualShortcuts.Concat(matches).ToArray();
    }

    private static int SubsequenceScore(string query, string candidate)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        int matched = 0;
        int score = 0;
        int previousIndex = -1;
        foreach (char character in query)
        {
            int index = candidate.IndexOf(character.ToString(), previousIndex + 1, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0)
            {
                return -1;
            }

            score += previousIndex + 1 == index ? 3 : 1;
            if (index == 0 || !char.IsLetterOrDigit(candidate[index - 1]))
            {
                score += 2;
            }

            previousIndex = index;
            matched++;
        }

        return score + (matched * 2) - candidate.Length / 20;
    }

    private void OnAppSelected(object sender, SelectionChangedEventArgs e)
    {
        if (AppResults.SelectedItem is AppShortcut app)
        {
            LaunchAppShortcut(app);
        }
    }

    private void LaunchAppShortcut(AppShortcut app)
    {
        if (app.Path.StartsWith("math:"))
        {
            Clipboard.SetText(app.Path[5..]);
            ShowCompactMessage($"Copied {app.Path[5..]}", Controls.SoulState.Acting);
        }
        else if (app.Path.StartsWith("cmd:"))
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/k {app.Path[4..]}") { UseShellExecute = true });
        }
        else
        {
            Process.Start(new ProcessStartInfo(app.Path) { UseShellExecute = true });
        }

        AppResults.SelectedItem = null;
        SetState(IslandState.Collapsed);
    }

    private void LoadWallpapers()
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Wallpapers");
        _wallpapers = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder)
                .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp")
                .Take(100)
                .ToArray()
            : [];
        WallpaperResults.ItemsSource = _wallpapers.Select(Path.GetFileName).ToArray();
    }

    private void OnWallpaperSelected(object sender, SelectionChangedEventArgs e)
    {
        int index = WallpaperResults.SelectedIndex;
        if (index < 0 || index >= _wallpapers.Count)
        {
            return;
        }

        string path = _wallpapers[index];
        if (!SystemParametersInfo(SpiSetDesktopWallpaper, 0, path, SpifUpdateIniFile | SpifSendChange))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        WallpaperResults.SelectedItem = null;
    }

    private void ProcessClipboardUpdate()
    {
        if (_suppressClipboardRecording)
        {
            return;
        }

        try
        {
            IDataObject? data = Clipboard.GetDataObject();
            if (data is null ||
                data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") ||
                IsExcludedFromHistory(data))
            {
                return;
            }

            if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
            {
                string text = Clipboard.GetText(TextDataFormat.UnicodeText);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                _database.AddClipboardText(text);
            }
            else if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource image)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var imageStream = new MemoryStream();
                encoder.Save(imageStream);
                byte[] imageBytes = imageStream.ToArray();
                try
                {
                    _database.AddClipboardImage(imageBytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(imageBytes);
                }
            }
            else
            {
                return;
            }

            LoadClipboardEntries();
            ClipboardPreview.Text = "Copied to Clipboard";
            if (_state is IslandState.Collapsed or IslandState.NowBar)
            {
                string snippet = _clipboardEntries.Count > 0 ? _clipboardEntries[0].PreviewText : "Content copied";
                ShowCompactMessage(
                    $"Copied · {(snippet.Length > 45 ? snippet[..45] + "…" : snippet)}",
                    Controls.SoulState.Acting,
                    IslandActivityKind.Clipboard);
            }
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException or
                                          InvalidOperationException or ArgumentOutOfRangeException or
                                          CryptographicException)
        {
            ClipboardPreview.Text = $"Clipboard unavailable: {exception.Message}";
        }
    }

    private static bool IsExcludedFromHistory(IDataObject data)
    {
        const string format = "CanIncludeInClipboardHistory";
        if (!data.GetDataPresent(format))
        {
            return false;
        }

        object? value = data.GetData(format);
        return value switch
        {
            bool include => !include,
            string include when bool.TryParse(include, out bool parsed) => !parsed,
            _ => true
        };
    }

    private void LoadClipboardEntries()
    {
        _clipboardEntries = _database.GetClipboardEntries();
        ClipboardHistoryList.ItemsSource = _clipboardEntries;
    }

    private void OnClipboardPinClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: long id } ||
            _clipboardEntries.FirstOrDefault(entry => entry.Id == id) is not ClipEntry entry)
        {
            return;
        }

        bool isPinned = !entry.IsPinned;
        if (!_database.SetClipboardEntryPinned(id, isPinned))
        {
            return;
        }

        LoadClipboardEntries();
        ClipboardPreview.Text = isPinned
            ? "Pinned. This copy stays until you unpin or delete it."
            : "Unpinned. It now counts as a recent copy.";
    }

    private void OnClipboardDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: long id } || !_database.DeleteClipboardEntry(id))
        {
            return;
        }

        LoadClipboardEntries();
        ClipboardPreview.Text = "Clipboard item deleted.";
    }

    private void OnClipboardItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: long id } ||
            _clipboardEntries.FirstOrDefault(entry => entry.Id == id) is not ClipEntry entry)
        {
            return;
        }

        try
        {
            IntPtr foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero || foregroundWindow == _windowHandle)
            {
                if (_lastActiveExternalWindow != IntPtr.Zero && _lastActiveExternalWindow != _windowHandle)
                {
                    SetForegroundWindow(_lastActiveExternalWindow);
                    Thread.Sleep(50);
                    foregroundWindow = GetForegroundWindow();
                }
            }

            if (foregroundWindow == IntPtr.Zero || foregroundWindow == _windowHandle)
            {
                throw new InvalidOperationException("Switch to your target application, then click Paste.");
            }

            _suppressClipboardRecording = true;
            if (entry.ContentType == "image/png")
            {
                byte[] imageBytes = Convert.FromBase64String(entry.TextOrImagePath);
                try
                {
                    using var imageStream = new MemoryStream(imageBytes, writable: false);
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = imageStream;
                    image.EndInit();
                    image.Freeze();
                    Clipboard.SetImage(image);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(imageBytes);
                }
            }
            else
            {
                Clipboard.SetText(entry.TextOrImagePath, TextDataFormat.UnicodeText);
            }

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _suppressClipboardRecording = false;
            };
            timer.Start();

            Input[] inputs =
            [
                KeyInput(0x11, 0),
                KeyInput(0x56, 0),
                KeyInput(0x56, 0x0002),
                KeyInput(0x11, 0x0002)
            ];
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            SetState(IslandState.Collapsed);
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                          System.Runtime.InteropServices.ExternalException or
                                          System.ComponentModel.Win32Exception or
                                          FormatException or IOException or
                                          NotSupportedException or InvalidDataException or ArgumentException)
        {
            _suppressClipboardRecording = false;
            ClipboardPreview.Text = $"Could not paste clipboard item: {exception.Message}";
        }
    }

    private static Input KeyInput(ushort virtualKey, uint flags) => new()
    {
        Type = 1,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = flags }
        }
    };

    private void OnLockClick(object sender, RoutedEventArgs e)
    {
        if (!LockWorkStation())
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private void OnSleepClick(object sender, RoutedEventArgs e)
    {
        if (!SetSuspendState(false, false, false))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private void OnQuietModeChanged(object sender, RoutedEventArgs e)
    {
        if (QuietModeToggle.IsChecked == true)
        {
            QuietModeToggle.Content = "Focus settings";
            Process.Start(new ProcessStartInfo("ms-settings:quietmomentshome") { UseShellExecute = true });
        }
    }

    private void OnRestartClick(object sender, RoutedEventArgs e) => RunPowerCommand(
        "Restart this PC?", "/r");

    private void OnShutdownClick(object sender, RoutedEventArgs e) => RunPowerCommand(
        "Shut down this PC?", "/s");

    private void RunPowerCommand(string confirmation, string command)
    {
        if (MessageBox.Show(confirmation, "Void", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        Process.Start(new ProcessStartInfo("shutdown.exe", $"{command} /t 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private sealed record AppShortcut(string Name, string Path);

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr windowHandle,
        int objectId, int childId, uint eventThreadId, uint eventTime)
    {
        if (windowHandle != IntPtr.Zero && windowHandle != _windowHandle)
        {
            _lastActiveExternalWindow = windowHandle;
        }

        Dispatcher.BeginInvoke(UpdateFullScreenVisibility);
    }

    private void UpdateFullScreenVisibility()
    {
        if (SHQueryUserNotificationState(out int state) != 0)
        {
            return;
        }

        bool fullScreen = state is QunsRunningD3dFullScreen;
        Visibility newVisibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
        if (Visibility != newVisibility)
        {
            Visibility = newVisibility;
            try { File.AppendAllText(Path.Combine(AppPaths.Root, "shell.log"), $"[{DateTime.UtcNow:u}] Visibility changed to {newVisibility} (SHQueryUserNotificationState={state})\n"); } catch { }
        }
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() => PositionAtTopCenter(force: true));
    }

    private void OnMouseMonitorChanged(int x, int y)
    {
        Dispatcher.BeginInvoke(() => PositionAtTopCenter());
    }

    private void PositionAtTopCenter(bool force = false)
    {
        System.Drawing.Point cursor = System.Windows.Forms.Cursor.Position;
        System.Windows.Forms.Screen screen = System.Windows.Forms.Screen.FromPoint(cursor);
        if (!force && string.Equals(_positionedMonitorDeviceName, screen.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _positionedMonitorDeviceName = screen.DeviceName;
        PositionWindowOnScreen(screen);
    }

    private void PositionWindowOnScreen(System.Windows.Forms.Screen screen)
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        const uint positionFlags = SwpNoSize | SwpNoActivate;
        System.Drawing.Rectangle bounds = screen.Bounds;

        // Move to the target monitor first so WPF can process a DPI change before centering.
        if (!SetWindowPos(_windowHandle, HwndTopmost, bounds.Left, bounds.Top, 0, 0, positionFlags) ||
            !GetWindowRect(_windowHandle, out NativeRect rect))
        {
            return;
        }

        int width = rect.Right - rect.Left;
        int centeredLeft = bounds.Left + (bounds.Width - width) / 2;
        SetWindowPos(_windowHandle, HwndTopmost, centeredLeft, bounds.Top, 0, 0, positionFlags);

        // Recenter after WPF applies the target monitor's DPI and updates the host size.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (_windowHandle == IntPtr.Zero ||
                !string.Equals(_positionedMonitorDeviceName, screen.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                !GetWindowRect(_windowHandle, out NativeRect updatedRect))
            {
                return;
            }

            int updatedWidth = updatedRect.Right - updatedRect.Left;
            int updatedLeft = bounds.Left + (bounds.Width - updatedWidth) / 2;
            SetWindowPos(_windowHandle, HwndTopmost, updatedLeft, bounds.Top, 0, 0, positionFlags);
        }));
    }

    private void OnMediaProgressTick(object? sender, EventArgs e)
    {
        var track = _mediaService.CurrentTrack;
        if (track is null || !track.IsPlaying || track.Duration <= TimeSpan.Zero)
        {
            UpdateMediaProgressTimerState();
            return;
        }

        TimeSpan currentPos = track.GetCurrentPosition();

        MusicTimelineProgress.Value = currentPos.TotalSeconds / track.Duration.TotalSeconds * 100;
        MusicTimelineText.Text = $"{FormatMediaTime(currentPos)} / {FormatMediaTime(track.Duration)}";
    }

    private void UpdateMediaProgressTimerState()
    {
        bool shouldRun = _state == IslandState.ExpandedDock &&
                         _selectedTab == 0 &&
                         _mediaService.CurrentTrack?.IsPlaying == true &&
                         _mediaService.CurrentTrack?.Duration > TimeSpan.Zero;
        if (shouldRun && !_mediaProgressTimer.IsEnabled)
        {
            _mediaProgressTimer.Start();
        }
        else if (!shouldRun && _mediaProgressTimer.IsEnabled)
        {
            _mediaProgressTimer.Stop();
        }
    }

    private void OnMediaTrackChanged(MediaTrackInfo? track)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (track is not null)
            {
                MusicTitleText.Text = track.Title;
                MusicArtistText.Text = string.IsNullOrWhiteSpace(track.Artist) ? "Playing" : track.Artist;
                MusicPlayPauseButton.Content = track.IsPlaying ? "⏸" : "▶";
                var artwork = LoadMediaArtwork(track.ThumbnailBytes);
                ApplyAlbumPalette(track);
                MusicAlbumArt.Source = artwork;
                MusicAlbumArtPlaceholder.Visibility = artwork is not null ? Visibility.Collapsed : Visibility.Visible;
                ControlsEqualizer.IsPlaying = track.IsPlaying;
                TimeSpan currentPos = track.GetCurrentPosition();
                MusicTimelineProgress.Value = track.Duration > TimeSpan.Zero
                    ? currentPos.TotalSeconds / track.Duration.TotalSeconds * 100
                    : 0;
                MusicTimelineText.Text = $"{FormatMediaTime(currentPos)} / {FormatMediaTime(track.Duration)}";
                CollapsedEqualizer.IsPlaying = track.IsPlaying;
                CollapsedEqualizer.Visibility = track.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
                CollapsedSoulName.Visibility = track.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
                ClockDivider.Visibility = track.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
                ClockText.Visibility = Visibility.Visible;
                MusicIslandCard.BorderBrush = track.IsPlaying
                    ? (System.Windows.Media.Brush)FindResource("AccentBorderBrush")
                    : (System.Windows.Media.Brush)FindResource("GlassCardBorderBrush");

            }
            else
            {
                MusicTitleText.Text = "No media playing";
                MusicArtistText.Text = "Spotify · Apple Music · Browser";
                MusicAlbumArt.Source = null;
                _albumPaletteKey = null;
                SetAlbumPalette(false, _albumPrimaryColor, _albumSecondaryColor);
                MusicAlbumArtPlaceholder.Visibility = Visibility.Visible;
                MusicPlayPauseButton.Content = "▶";
                ControlsEqualizer.IsPlaying = false;
                MusicTimelineProgress.Value = 0;
                MusicTimelineText.Text = "--:-- / --:--";
                CollapsedEqualizer.IsPlaying = false;
                CollapsedEqualizer.Visibility = Visibility.Collapsed;
                CollapsedSoulName.Visibility = Visibility.Visible;
                ClockDivider.Visibility = Visibility.Visible;
                ClockText.Visibility = Visibility.Visible;
                MusicIslandCard.BorderBrush = (System.Windows.Media.Brush)FindResource("GlassCardBorderBrush");
            }

            UpdateMediaProgressTimerState();
            UpdateNowBarContent();
            UpdatePersistentIslandState();
            SyncMediaActivity(_mediaService.CurrentTrack);
        });
    }

    private static BitmapImage? LoadMediaArtwork(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 320;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void ApplyAlbumPalette(MediaTrackInfo track)
    {
        byte[]? bytes = track.ThumbnailBytes;
        if (bytes is null || bytes.Length == 0)
        {
            _albumPaletteKey = null;
            SetAlbumPalette(false, _albumPrimaryColor, _albumSecondaryColor);
            return;
        }

        string key = $"{track.AlbumTitle}\u001f{track.Artist}";
        if (!string.Equals(key, _albumPaletteKey, StringComparison.Ordinal) ||
            !ControlsEqualizer.HasAlbumPalette)
        {
            _albumPaletteKey = key;
            if (TryExtractAlbumPalette(bytes, out Color primary, out Color secondary))
            {
                _albumPrimaryColor = primary;
                _albumSecondaryColor = secondary;
                SetAlbumPalette(true, primary, secondary);
                return;
            }

            SetAlbumPalette(false, _albumPrimaryColor, _albumSecondaryColor);
            return;
        }

        SetAlbumPalette(true, _albumPrimaryColor, _albumSecondaryColor);
    }

    private void SetAlbumPalette(bool enabled, Color primary, Color secondary)
    {
        ControlsEqualizer.AlbumPrimaryColor = primary;
        ControlsEqualizer.AlbumSecondaryColor = secondary;
        ControlsEqualizer.HasAlbumPalette = enabled;
        CollapsedEqualizer.AlbumPrimaryColor = primary;
        CollapsedEqualizer.AlbumSecondaryColor = secondary;
        CollapsedEqualizer.HasAlbumPalette = enabled;
    }

    private static bool TryExtractAlbumPalette(byte[] bytes, out Color primary, out Color secondary)
    {
        primary = Color.FromRgb(56, 189, 248);
        secondary = Color.FromRgb(129, 140, 248);
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 32;
            image.StreamSource = stream;
            image.EndInit();
            var pixels = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int width = pixels.PixelWidth;
            int height = pixels.PixelHeight;
            if (width == 0 || height == 0 || width > 64 || height > 64)
            {
                return false;
            }

            byte[] buffer = new byte[width * height * 4];
            pixels.CopyPixels(buffer, width * 4, 0);
            var histogram = new Dictionary<int, int>();
            for (int index = 0; index < buffer.Length; index += 4)
            {
                if (buffer[index + 3] < 200)
                {
                    continue;
                }

                int blue = buffer[index];
                int green = buffer[index + 1];
                int red = buffer[index + 2];
                int maximum = Math.Max(red, Math.Max(green, blue));
                int minimum = Math.Min(red, Math.Min(green, blue));
                double saturation = maximum == 0 ? 0 : (maximum - minimum) / (double)maximum;
                if (maximum < 36 || maximum > 250 || saturation < 0.16)
                {
                    continue;
                }

                int key = ((red >> 4) << 8) | ((green >> 4) << 4) | (blue >> 4);
                histogram[key] = histogram.GetValueOrDefault(key) + 1 + (int)(saturation * 3);
            }

            int[] selected = histogram.OrderByDescending(entry => entry.Value).Take(2).Select(entry => entry.Key).ToArray();
            if (selected.Length == 0)
            {
                return false;
            }

            primary = LiftPaletteColor(selected[0]);
            secondary = selected.Length > 1
                ? LiftPaletteColor(selected[1])
                : Color.FromRgb(primary.B, primary.R, primary.G);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Color LiftPaletteColor(int key)
    {
        byte red = (byte)Math.Min(255, (((key >> 8) & 0xF) * 17 * 3 / 4) + 64);
        byte green = (byte)Math.Min(255, (((key >> 4) & 0xF) * 17 * 3 / 4) + 64);
        byte blue = (byte)Math.Min(255, ((key & 0xF) * 17 * 3 / 4) + 64);
        return Color.FromRgb(red, green, blue);
    }

    private static string FormatMediaTime(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    private void OnMediaPlaybackStateChanged(bool isPlaying)
    {
        Dispatcher.BeginInvoke(() =>
        {
            MusicPlayPauseButton.Content = isPlaying ? "⏸" : "▶";
            ControlsEqualizer.IsPlaying = isPlaying;
            CollapsedEqualizer.IsPlaying = isPlaying;
            CollapsedEqualizer.Visibility = isPlaying ? Visibility.Visible : Visibility.Collapsed;
            CollapsedSoulName.Visibility = isPlaying ? Visibility.Collapsed : Visibility.Visible;
            ClockDivider.Visibility = isPlaying ? Visibility.Collapsed : Visibility.Visible;
            ClockText.Visibility = Visibility.Visible;
            MusicIslandCard.BorderBrush = isPlaying
                ? (System.Windows.Media.Brush)FindResource("AccentBorderBrush")
                : (System.Windows.Media.Brush)FindResource("GlassCardBorderBrush");
            UpdateMediaProgressTimerState();
            UpdateNowBarContent();
            UpdatePersistentIslandState();
            SyncMediaActivity(_mediaService.CurrentTrack);
        });
    }

    private async void OnMusicPlayPauseClick(object sender, RoutedEventArgs e)
    {
        await _mediaService.TogglePlayPauseAsync();
    }

    private async void OnMusicPrevClick(object sender, RoutedEventArgs e)
    {
        await _mediaService.PreviousAsync();
    }

    private async void OnMusicNextClick(object sender, RoutedEventArgs e)
    {
        await _mediaService.NextAsync();
    }

    private Task<bool> PromptPermissionApprovalAsync(
        ToolDefinition tool,
        System.Text.Json.JsonElement arguments,
        ToolExecutionContext context)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.BeginInvoke(() =>
        {
            _permissionTcs?.TrySetResult(false);
            _permissionTcs = tcs;

            PermissionToolName.Text = $"Tool: {tool.Name} ({tool.RiskLevel} Risk)";
            PermissionDescription.Text = tool.Description;
            PermissionArguments.Text = arguments.GetRawText();
            PermissionContextAlert.Visibility = (context.IsUntrustedInput || _gatekeeper.IsContextUntrusted)
                ? Visibility.Visible : Visibility.Collapsed;
            PermissionSoul.SoulState = Controls.SoulState.Thinking;

            _activityCoordinator.Publish(
                "gatekeeper",
                IslandActivityKind.Gatekeeper,
                "Security confirmation required",
                $"{tool.Name}: {tool.Description}");
            SetState(IslandState.PermissionPrompt);
        });

        return tcs.Task;
    }

    private void OnPermissionAllowClick(object sender, RoutedEventArgs e)
    {
        TaskCompletionSource<bool>? permission = _permissionTcs;
        _permissionTcs = null;
        _activityCoordinator.Clear("gatekeeper");
        permission?.TrySetResult(true);
        SetState(IslandState.Collapsed);
    }

    private void OnPermissionDenyClick(object sender, RoutedEventArgs e)
    {
        TriggerHorizontalNudge();
        TaskCompletionSource<bool>? permission = _permissionTcs;
        _permissionTcs = null;
        _activityCoordinator.Clear("gatekeeper");
        permission?.TrySetResult(false);
        SetState(IslandState.Collapsed);
    }

    private void OnIslandPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _state == IslandState.ExpandedDock)
        {
            SetState(IslandState.Collapsed);
            e.Handled = true;
            return;
        }

        if (_state != IslandState.PermissionPrompt)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            OnPermissionAllowClick(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            OnPermissionDenyClick(sender, e);
            e.Handled = true;
        }
    }

    private void LoadWifiInfo()
    {
        Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo("netsh.exe", "wlan show interfaces")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                if (process is not null)
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(1000);
                    foreach (string line in output.Split('\n'))
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) &&
                            !trimmed.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
                        {
                            int colon = trimmed.IndexOf(':');
                            if (colon >= 0 && colon + 1 < trimmed.Length)
                            {
                                string ssid = trimmed[(colon + 1)..].Trim();
                                if (!string.IsNullOrEmpty(ssid))
                                {
                                    Dispatcher.BeginInvoke(() =>
                                    {
                                        WifiTileButton.Content = $"Wi-Fi · {ssid}\nClick to turn off";
                                        WifiTileButton.Background = (System.Windows.Media.Brush)FindResource("AccentGlassBrush");
                                        WifiTileButton.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBorderBrush");
                                    });
                                    return;
                                }
                            }
                        }
                    }
                }

                Dispatcher.BeginInvoke(() =>
                {
                    bool isEnabled = _systemControls.GetWifiEnabled();
                    WifiTileButton.Content = isEnabled
                        ? "Wi-Fi connected\nClick to turn off"
                        : "Wi-Fi off\nClick to turn on";
                    WifiTileButton.Background = isEnabled
                        ? (System.Windows.Media.Brush)FindResource("AccentGlassBrush")
                        : (System.Windows.Media.Brush)FindResource("GlassCardBrush");
                    WifiTileButton.BorderBrush = isEnabled
                        ? (System.Windows.Media.Brush)FindResource("AccentBorderBrush")
                        : (System.Windows.Media.Brush)FindResource("GlassCardBorderBrush");
                });
            }
            catch (Exception exception)
            {
                Dispatcher.BeginInvoke(() =>
                    WifiTileButton.Content = $"Wi-Fi unavailable\n{exception.Message}");
            }
        });
    }

    private async void LoadBluetoothInfo()
    {
        try
        {
            bool enabled = await _systemControls.GetBluetoothEnabledAsync();
            BluetoothTileButton.Content = enabled
                ? "Bluetooth on\nClick to turn off"
                : "Bluetooth off\nClick to turn on";
            BluetoothTileButton.Background = enabled
                ? (System.Windows.Media.Brush)FindResource("AccentGlassBrush")
                : (System.Windows.Media.Brush)FindResource("GlassCardBrush");
            BluetoothTileButton.BorderBrush = enabled
                ? (System.Windows.Media.Brush)FindResource("AccentBorderBrush")
                : (System.Windows.Media.Brush)FindResource("GlassCardBorderBrush");
        }
        catch (Exception exception)
        {
            BluetoothTileButton.Content = $"Bluetooth unavailable\n{exception.Message}";
        }
    }

    private async void OnWifiToggleClick(object sender, RoutedEventArgs e)
    {
        WifiTileButton.IsEnabled = false;
        try
        {
            bool enabled = !_systemControls.GetWifiEnabled();
            await _systemControls.SetWifiEnabledAsync(enabled);
            WifiTileButton.Content = enabled ? "Wi-Fi on" : "Wi-Fi off";
            WifiTileButton.Background = enabled
                ? (System.Windows.Media.Brush)FindResource("AccentGlassBrush")
                : (System.Windows.Media.Brush)FindResource("GlassCardBrush");
            WifiTileButton.BorderBrush = enabled
                ? (System.Windows.Media.Brush)FindResource("AccentBorderBrush")
                : (System.Windows.Media.Brush)FindResource("GlassCardBorderBrush");
            if (enabled)
            {
                LoadWifiInfo();
            }
        }
        catch (Exception exception)
        {
            WifiTileButton.Content = $"Wi-Fi change failed\n{exception.Message}";
        }
        finally
        {
            WifiTileButton.IsEnabled = true;
        }
    }

    private async void OnBluetoothToggleClick(object sender, RoutedEventArgs e)
    {
        BluetoothTileButton.IsEnabled = false;
        try
        {
            bool enabled = !await _systemControls.GetBluetoothEnabledAsync();
            await _systemControls.SetBluetoothEnabledAsync(enabled);
            BluetoothTileButton.Content = enabled ? "Bluetooth on" : "Bluetooth off";
            BluetoothTileButton.Background = enabled
                ? (System.Windows.Media.Brush)FindResource("AccentGlassBrush")
                : (System.Windows.Media.Brush)FindResource("GlassCardBrush");
            BluetoothTileButton.BorderBrush = enabled
                ? (System.Windows.Media.Brush)FindResource("AccentBorderBrush")
                : (System.Windows.Media.Brush)FindResource("GlassCardBorderBrush");
        }
        catch (Exception exception)
        {
            BluetoothTileButton.Content = $"Bluetooth change failed\n{exception.Message}";
        }
        finally
        {
            BluetoothTileButton.IsEnabled = true;
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _idleTrimTimer.Stop();
        _mediaProgressTimer.Stop();
        _clockTimer.Stop();
        _timerDisplayTimer.Stop();
        _mediaProgressTimer.Tick -= OnMediaProgressTick;
        _clockTimer.Tick -= OnClockTick;
        _timerDisplayTimer.Tick -= OnTimerDisplayTick;
        if (_windowHandle != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(_windowHandle);
        }
        _animator.Stop();
        _globalInput.Dispose();
        _mouseMonitorTracker.Dispose();
        _voiceSessionCancellation?.Cancel();
        await StopLiveVoiceSessionAsync();
        _dictation.Dispose();
        await _circleOverlay.CancelCaptureAsync();
        _circleOverlay.Dispose();
        _permissionTcs?.TrySetResult(false);
        _gatekeeper.Dispose();
        _countdownTimer.Completed -= OnCountdownTimerCompleted;
        _countdownTimer.Dispose();
        await _mediaService.DisposeAsync();
        _toolExecution.Dispose();

        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
        }

        if (_scheduler is not null)
        {
            await _scheduler.DisposeAsync();
        }

        await _ipcServer.DisposeAsync();
    }

    private static IntPtr GetWindowLong(IntPtr handle, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(handle, index) : GetWindowLong32(handle, index);

    private static IntPtr SetWindowLong(IntPtr handle, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(handle, index, value) : SetWindowLong32(handle, index, value);
}
