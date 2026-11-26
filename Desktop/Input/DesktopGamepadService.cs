using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Windows.Gaming.Input;
using Youtube_TV.Input;

namespace Youtube_TV.Desktop.Input
{
    /// <summary>
    /// Coordinates multi-backend controller input for Windows Desktop and Windows 11 Xbox Mode
    /// (Full Screen Experience) on handhelds like the ASUS ROG Xbox Ally X:
    /// 1. Native XInput (xinput1_4 / xinput9_1_0)
    /// 2. Native Windows.Gaming.Input.Gamepad (GameInput-backed API used in Xbox Mode)
    /// 3. W3C Web Gamepad API fallback (PlayStation DualSense / DualShock 4 / Switch Pro,
    ///    and automatic fallback if native APIs report zeroed readings)
    ///
    /// Uses live Win32 foreground HWND checks instead of sticky WPF Deactivated events so
    /// Xbox Mode Task Switcher, Game Bar, and WebView2 child HWND focus changes never stall polling.
    /// </summary>
    public sealed class DesktopGamepadService : IDisposable
    {
        private const int MaxXInputControllers = 4;
        private const uint ErrorSuccess = 0;
        private const uint GaRoot = 2;

        private const ushort XInputDPadUp = 0x0001;
        private const ushort XInputDPadDown = 0x0002;
        private const ushort XInputDPadLeft = 0x0004;
        private const ushort XInputDPadRight = 0x0008;
        private const ushort XInputStart = 0x0010;
        private const ushort XInputBack = 0x0020;
        private const ushort XInputLeftThumb = 0x0040;
        private const ushort XInputRightThumb = 0x0080;
        private const ushort XInputLeftShoulder = 0x0100;
        private const ushort XInputRightShoulder = 0x0200;
        private const ushort XInputA = 0x1000;
        private const ushort XInputB = 0x2000;
        private const ushort XInputX = 0x4000;
        private const ushort XInputY = 0x8000;

        private static readonly TimeSpan ActivePollInterval = TimeSpan.FromMilliseconds(16);
        private static readonly TimeSpan BackgroundCheckInterval = TimeSpan.FromMilliseconds(150);
        private static readonly TimeSpan IdleScanInterval = TimeSpan.FromMilliseconds(500);

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputGamepad
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputState
        {
            public uint dwPacketNumber;
            public XInputGamepad Gamepad;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState14(uint dwUserIndex, out XInputState pState);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState910(uint dwUserIndex, out XInputState pState);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        private readonly ControllerInputProcessor _processor = new ControllerInputProcessor();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly DispatcherTimer _pollTimer;
        private readonly List<ControllerReading> _readingScratch = new List<ControllerReading>(8);
        private readonly uint _currentProcessId;

        private IntPtr _windowHandle = IntPtr.Zero;
        private uint _webViewBrowserProcessId;
        private bool _useXInput910Fallback;
        private bool _xInputUnavailable;
        private bool _wgiAvailable;
        private bool _isForeground = true;
        private bool _isDisposed;
        private int _nativeGamepadCount;
        private bool _nativeInputVerifiedInSession;
        private ControllerReading _webReading;
        private bool _hasActiveWebInput;

        public event EventHandler<ControllerActionEvent> ActionTriggered;
        public event EventHandler<bool> ForegroundChanged;
        public event EventHandler<bool> WebFallbackSuppressedChanged;
        public event EventHandler<bool> ControllerConnectionToastRequested;
        public event EventHandler FullscreenToggleRequested;
        public event EventHandler HelpOverlayDismissedFromWeb;

        public DesktopGamepadService(Dispatcher dispatcher)
        {
            if (dispatcher == null)
            {
                throw new ArgumentNullException(nameof(dispatcher));
            }

            _currentProcessId = (uint)Process.GetCurrentProcess().Id;

            InitializeWindowsGamingInput();

            _pollTimer = new DispatcherTimer(DispatcherPriority.Input, dispatcher);
            _pollTimer.Tick += PollTimer_Tick;

            ControllerReading initialReading;
            _nativeGamepadCount = ReadNativeControllers(out initialReading);
            UpdatePollingTimer();
        }

        public int NativeGamepadCount
        {
            get { return _nativeGamepadCount; }
        }

        public bool IsWebFallbackSuppressed
        {
            get { return _nativeInputVerifiedInSession; }
        }

        public bool IsForeground
        {
            get { return _isForeground; }
        }

        public void AttachWindow(IntPtr windowHandle)
        {
            _windowHandle = windowHandle;
            EvaluateForegroundState();
        }

        public void SetWebViewBrowserProcessId(uint browserProcessId)
        {
            _webViewBrowserProcessId = browserProcessId;
            EvaluateForegroundState();
        }

        public void NotifyWindowFocusHint()
        {
            EvaluateForegroundState();
        }

        /// <summary>
        /// Script injected into WebView2 on document creation to provide:
        /// 1. W3C Web Gamepad API fallback (PlayStation / Switch Pro, and automatic backup if native APIs
        ///    do not deliver input in Xbox Mode).
        /// 2. Persistent HTML5 video volume control across autoplay transitions.
        /// 3. F11 / Alt+Enter fullscreen toggle forwarding.
        /// 4. Shadow DOM toast and controller controls overlay rendered cleanly above WebView2 video.
        /// </summary>
        public static string DesktopWebBridgeScript
        {
            get
            {
                return @"
(function() {
    if (window.__ytTvControllerBridgeInstalled) { return; }
    window.__ytTvControllerBridgeInstalled = true;

    var suppressWebGamepad = false;
    var appForeground = true;
    var rafId = 0;
    var lastPayload = '';
    var desiredVolume = null;
    var toastTimer = 0;
    var shadowRoot = null;
    var toastEl = null;
    var toastTextEl = null;
    var toastBarWrapEl = null;
    var toastBarFillEl = null;
    var helpEl = null;

    function post(msg) {
        try {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(msg);
            }
        } catch (err) { }
    }

    function ensureOverlay() {
        if (shadowRoot) { return true; }
        var container = document.documentElement || document.body;
        if (!container) { return false; }

        var host = document.createElement('div');
        host.id = '__yt-tv-desktop-overlay';
        host.style.cssText = 'position:fixed;inset:0;pointer-events:none;z-index:2147483647;';
        container.appendChild(host);

        shadowRoot = host.attachShadow({ mode: 'open' });
        shadowRoot.innerHTML = [
            '<style>',
            '  .toast {',
            '    position: fixed; left: 50%; bottom: 40px; transform: translateX(-50%);',
            '    min-width: 240px; padding: 12px 22px; border-radius: 12px;',
            '    background: rgba(24, 24, 24, 0.92); border: 1px solid rgba(255, 255, 255, 0.22);',
            '    color: #fff; font-family: ""Segoe UI"", Arial, sans-serif; text-align: center;',
            '    box-shadow: 0 8px 30px rgba(0,0,0,0.55); opacity: 0; transition: opacity 150ms ease;',
            '    pointer-events: none;',
            '  }',
            '  .toast.visible { opacity: 1; }',
            '  .toast-text { font-size: 18px; font-weight: 600; letter-spacing: 0.2px; }',
            '  .bar-wrap { margin-top: 8px; height: 6px; border-radius: 3px; background: rgba(255,255,255,0.24); overflow: hidden; display: none; }',
            '  .bar-fill { height: 100%; width: 100%; background: #ff0000; border-radius: 3px; }',
            '  .help-backdrop {',
            '    position: fixed; inset: 0; background: rgba(0, 0, 0, 0.78);',
            '    display: none; align-items: center; justify-content: center; pointer-events: auto;',
            '    font-family: ""Segoe UI"", Arial, sans-serif;',
            '  }',
            '  .help-backdrop.visible { display: flex; }',
            '  .help-card {',
            '    max-width: 780px; width: 88vw; padding: 28px 34px; border-radius: 16px;',
            '    background: rgba(24, 24, 24, 0.96); border: 1px solid rgba(255,255,255,0.2);',
            '    color: #fff; box-shadow: 0 16px 48px rgba(0,0,0,0.7);',
            '  }',
            '  .help-title { font-size: 26px; font-weight: 700; margin: 0 0 6px 0; }',
            '  .help-sub { font-size: 15px; color: rgba(255,255,255,0.7); margin: 0 0 22px 0; }',
            '  .help-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 12px 32px; }',
            '  .row { display: flex; justify-content: space-between; gap: 16px; font-size: 16px; }',
            '  .key { color: #ff4e45; font-weight: 600; white-space: nowrap; }',
            '  .act { color: #ffffff; text-align: right; }',
            '</style>',
            '<div class=""toast"" id=""toast"">',
            '  <div class=""toast-text"" id=""toastText""></div>',
            '  <div class=""bar-wrap"" id=""barWrap""><div class=""bar-fill"" id=""barFill""></div></div>',
            '</div>',
            '<div class=""help-backdrop"" id=""help"">',
            '  <div class=""help-card"">',
            '    <div class=""help-title"">Controller Controls</div>',
            '    <div class=""help-sub"">Press L3, B, or click anywhere to close</div>',
            '    <div class=""help-grid"">',
            '      <div class=""row""><span class=""key"">D-Pad / Left Stick</span><span class=""act"">Navigate</span></div>',
            '      <div class=""row""><span class=""key"">LT / RT</span><span class=""act"">Rewind / Fast Forward</span></div>',
            '      <div class=""row""><span class=""key"">A / Cross</span><span class=""act"">Select (hold for menu)</span></div>',
            '      <div class=""row""><span class=""key"">LB / RB</span><span class=""act"">Previous / Next Video</span></div>',
            '      <div class=""row""><span class=""key"">B / Circle</span><span class=""act"">Back / Close</span></div>',
            '      <div class=""row""><span class=""key"">Right Stick Up/Down</span><span class=""act"">Volume Up / Down</span></div>',
            '      <div class=""row""><span class=""key"">X / Square / Menu</span><span class=""act"">Play / Pause</span></div>',
            '      <div class=""row""><span class=""key"">R3 (Right Stick)</span><span class=""act"">Mute / Unmute</span></div>',
            '      <div class=""row""><span class=""key"">Y / Triangle</span><span class=""act"">Search</span></div>',
            '      <div class=""row""><span class=""key"">L3 (Left Stick)</span><span class=""act"">Show / Hide Controls</span></div>',
            '      <div class=""row""><span class=""key"">View / Select</span><span class=""act"">Toggle Fullscreen</span></div>',
            '      <div class=""row""><span class=""key"">F11 / Alt+Enter</span><span class=""act"">Toggle Fullscreen (Keyboard)</span></div>',
            '    </div>',
            '  </div>',
            '</div>'
        ].join('');

        toastEl = shadowRoot.getElementById('toast');
        toastTextEl = shadowRoot.getElementById('toastText');
        toastBarWrapEl = shadowRoot.getElementById('barWrap');
        toastBarFillEl = shadowRoot.getElementById('barFill');
        helpEl = shadowRoot.getElementById('help');
        helpEl.addEventListener('click', function() {
            window.__ytTvSetHelpVisible(false);
            post('help_closed');
        });
        return true;
    }

    window.__ytTvShowToast = function(message, progressPercent, durationMs) {
        if (!ensureOverlay()) { return; }
        toastTextEl.textContent = message || '';
        if (typeof progressPercent === 'number' && progressPercent >= 0) {
            toastBarWrapEl.style.display = 'block';
            toastBarFillEl.style.width = Math.max(0, Math.min(100, progressPercent)) + '%';
        } else {
            toastBarWrapEl.style.display = 'none';
        }
        toastEl.classList.add('visible');
        if (toastTimer) { window.clearTimeout(toastTimer); }
        toastTimer = window.setTimeout(function() {
            toastEl.classList.remove('visible');
            toastTimer = 0;
        }, durationMs || 2000);
    };

    window.__ytTvSetHelpVisible = function(visible) {
        if (!ensureOverlay()) { return; }
        if (visible) {
            helpEl.classList.add('visible');
        } else {
            helpEl.classList.remove('visible');
        }
    };

    window.__ytTvSetWebFallbackSuppressed = function(suppressed) {
        suppressWebGamepad = !!suppressed;
        if (suppressWebGamepad && lastPayload !== '') {
            lastPayload = '';
        }
        updateLoop();
    };

    window.__ytTvSetForeground = function(isForeground) {
        appForeground = !!isForeground;
        if (!appForeground) {
            lastPayload = '';
        }
        updateLoop();
    };

    window.__ytTvAdjustVolume = function(delta) {
        var video = document.querySelector('video');
        var baseVol = video ? video.volume : (desiredVolume !== null ? desiredVolume : 1.0);
        var nextVol = Math.max(0, Math.min(1, Math.round((baseVol + delta) * 20) / 20));
        desiredVolume = nextVol;
        if (video) {
            video.muted = false;
            video.volume = nextVol;
        }
        return Math.round(nextVol * 100);
    };

    document.addEventListener('play', function(e) {
        if (desiredVolume !== null && e && e.target && e.target.tagName === 'VIDEO') {
            e.target.volume = desiredVolume;
        }
    }, true);

    window.addEventListener('keydown', function(e) {
        if (e.key === 'F11' || (e.altKey && e.key === 'Enter')) {
            e.preventDefault();
            e.stopPropagation();
            post('toggle_fullscreen');
        }
    }, true);

    function hasWebGamepads() {
        if (!navigator.getGamepads) { return false; }
        var pads = navigator.getGamepads();
        if (!pads) { return false; }
        for (var i = 0; i < pads.length; i++) {
            if (pads[i] && pads[i].connected && pads[i].mapping === 'standard') {
                return true;
            }
        }
        return false;
    }

    function btnPressed(buttons, idx) {
        return buttons && buttons[idx] && buttons[idx].pressed;
    }

    function btnValue(buttons, idx) {
        if (!buttons || !buttons[idx]) { return 0; }
        var b = buttons[idx];
        return typeof b.value === 'number' ? b.value : (b.pressed ? 1 : 0);
    }

    function pollWebGamepads() {
        rafId = 0;
        if (suppressWebGamepad || !appForeground || !hasWebGamepads()) {
            return;
        }

        var pads = navigator.getGamepads();
        var mask = 0;
        var lx = 0, ly = 0, rx = 0, ry = 0, lt = 0, rt = 0;
        var bestLMag = 0, bestRMag = 0;

        for (var i = 0; i < pads.length; i++) {
            var p = pads[i];
            if (!p || !p.connected || p.mapping !== 'standard') { continue; }
            var b = p.buttons;
            if (btnPressed(b, 9))  mask |= 1;
            if (btnPressed(b, 8))  mask |= 2;
            if (btnPressed(b, 0))  mask |= 4;
            if (btnPressed(b, 1))  mask |= 8;
            if (btnPressed(b, 2))  mask |= 16;
            if (btnPressed(b, 3))  mask |= 32;
            if (btnPressed(b, 12)) mask |= 64;
            if (btnPressed(b, 13)) mask |= 128;
            if (btnPressed(b, 14)) mask |= 256;
            if (btnPressed(b, 15)) mask |= 512;
            if (btnPressed(b, 4))  mask |= 1024;
            if (btnPressed(b, 5))  mask |= 2048;
            if (btnPressed(b, 10)) mask |= 4096;
            if (btnPressed(b, 11)) mask |= 8192;

            var tL = btnValue(b, 6);
            var tR = btnValue(b, 7);
            if (tL > lt) lt = tL;
            if (tR > rt) rt = tR;

            var a = p.axes || [];
            var clx = a.length > 0 ? a[0] : 0;
            var cly = a.length > 1 ? -a[1] : 0;
            var crx = a.length > 2 ? a[2] : 0;
            var cry = a.length > 3 ? -a[3] : 0;

            var lMag = clx * clx + cly * cly;
            if (lMag > bestLMag) { bestLMag = lMag; lx = clx; ly = cly; }
            var rMag = crx * crx + cry * cry;
            if (rMag > bestRMag) { bestRMag = rMag; rx = crx; ry = cry; }
        }

        var payload = 'gp:' + mask + '|' +
            lx.toFixed(3) + '|' + ly.toFixed(3) + '|' +
            rx.toFixed(3) + '|' + ry.toFixed(3) + '|' +
            lt.toFixed(3) + '|' + rt.toFixed(3);

        if (payload !== lastPayload) {
            lastPayload = payload;
            post(payload);
        }

        rafId = window.requestAnimationFrame(pollWebGamepads);
    }

    function updateLoop() {
        if (!suppressWebGamepad && appForeground && hasWebGamepads()) {
            if (!rafId) {
                rafId = window.requestAnimationFrame(pollWebGamepads);
            }
        } else if (rafId) {
            window.cancelAnimationFrame(rafId);
            rafId = 0;
        }
    }

    window.addEventListener('gamepadconnected', function() {
        if (!suppressWebGamepad) {
            post('gp_connected');
        }
        updateLoop();
    });

    window.addEventListener('gamepaddisconnected', function() {
        if (!suppressWebGamepad && !hasWebGamepads()) {
            lastPayload = '';
            post('gp:0|0|0|0|0|0|0');
            post('gp_disconnected');
        }
        updateLoop();
    });

    updateLoop();
})();";
            }
        }

        public void HandleWebMessage(string message)
        {
            if (_isDisposed || string.IsNullOrEmpty(message))
            {
                return;
            }

            if (message == "toggle_fullscreen")
            {
                EventHandler fsHandler = FullscreenToggleRequested;
                if (fsHandler != null) fsHandler(this, EventArgs.Empty);
                return;
            }

            if (message == "help_closed")
            {
                EventHandler helpHandler = HelpOverlayDismissedFromWeb;
                if (helpHandler != null) helpHandler(this, EventArgs.Empty);
                return;
            }

            if (message == "gp_connected")
            {
                if (_nativeGamepadCount == 0)
                {
                    RaiseConnectionToast(true);
                }
                return;
            }

            if (message == "gp_disconnected")
            {
                _webReading = new ControllerReading();
                _hasActiveWebInput = false;
                if (!_nativeInputVerifiedInSession && _nativeGamepadCount == 0)
                {
                    EmitEvents(_processor.Reset());
                    RaiseConnectionToast(false);
                }
                UpdatePollingTimer();
                return;
            }

            if (!_isForeground || _nativeInputVerifiedInSession)
            {
                return;
            }

            ControllerReading parsed;
            if (!ControllerInputProcessor.TryParseWebGamepadMessage(message, out parsed))
            {
                return;
            }

            _webReading = parsed;
            _hasActiveWebInput = !ControllerInputProcessor.IsNeutral(parsed);

            EmitEvents(_processor.Update(_webReading, _clock.ElapsedMilliseconds));
            UpdatePollingTimer();
        }

        private void InitializeWindowsGamingInput()
        {
            try
            {
                // Accessing Gamepad.Gamepads and subscribing to Added/Removed ensures
                // WinRT initializes its GameInput-backed gamepad manager on desktop apps.
                var unused = Gamepad.Gamepads.Count;
                Gamepad.GamepadAdded += WgiGamepad_GamepadAdded;
                Gamepad.GamepadRemoved += WgiGamepad_GamepadRemoved;
                _wgiAvailable = true;
            }
            catch (Exception)
            {
                _wgiAvailable = false;
            }
        }

        private void WgiGamepad_GamepadAdded(object sender, Gamepad gamepad)
        {
        }

        private void WgiGamepad_GamepadRemoved(object sender, Gamepad gamepad)
        {
        }

        private void PollTimer_Tick(object sender, EventArgs e)
        {
            if (_isDisposed)
            {
                return;
            }

            EvaluateForegroundState();
            if (!_isForeground)
            {
                return;
            }

            ControllerReading nativeCombined;
            int nativeCount = ReadNativeControllers(out nativeCombined);

            if (nativeCount != _nativeGamepadCount)
            {
                int previous = _nativeGamepadCount;
                _nativeGamepadCount = nativeCount;

                if (nativeCount > 0 && previous == 0)
                {
                    RaiseConnectionToast(true);
                }
                else if (nativeCount == 0 && previous > 0)
                {
                    if (_nativeInputVerifiedInSession)
                    {
                        SetWebFallbackSuppressed(false);
                    }
                    EmitEvents(_processor.Reset());
                    RaiseConnectionToast(false);
                }

                UpdatePollingTimer();
            }

            bool nativeHasSignal = nativeCount > 0 && !ControllerInputProcessor.IsNeutral(nativeCombined);
            if (nativeHasSignal && !_nativeInputVerifiedInSession)
            {
                _webReading = new ControllerReading();
                _hasActiveWebInput = false;
                SetWebFallbackSuppressed(true);
            }

            if (_nativeInputVerifiedInSession && nativeCount > 0)
            {
                EmitEvents(_processor.Update(nativeCombined, _clock.ElapsedMilliseconds));
            }
            else if (_hasActiveWebInput || _processor.HasHeldActions)
            {
                // Before native input is verified in this session, combine native + web readings
                // idempotently so either backend works seamlessly (including Xbox Mode / GameInput).
                ControllerReading merged = _hasActiveWebInput
                    ? ControllerInputProcessor.CombineReadings(new ControllerReading[] { nativeCombined, _webReading })
                    : nativeCombined;

                EmitEvents(_processor.Update(merged, _clock.ElapsedMilliseconds));
                if (nativeCount == 0 && !_hasActiveWebInput && !_processor.HasHeldActions)
                {
                    UpdatePollingTimer();
                }
            }
        }

        private void EvaluateForegroundState()
        {
            bool nowForeground = CheckIsAppForeground();
            if (nowForeground == _isForeground)
            {
                return;
            }

            _isForeground = nowForeground;

            if (!_isForeground)
            {
                _webReading = new ControllerReading();
                _hasActiveWebInput = false;
                EmitEvents(_processor.Reset());
            }
            else
            {
                // Re-allow web fallback until native confirms active input in the resumed foreground session.
                if (_nativeInputVerifiedInSession)
                {
                    SetWebFallbackSuppressed(false);
                }
            }

            EventHandler<bool> fgHandler = ForegroundChanged;
            if (fgHandler != null)
            {
                fgHandler(this, _isForeground);
            }

            UpdatePollingTimer();
        }

        private bool CheckIsAppForeground()
        {
            if (_windowHandle == IntPtr.Zero)
            {
                return true;
            }

            if (!IsWindowVisible(_windowHandle) || IsIconic(_windowHandle))
            {
                return false;
            }

            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero)
            {
                // During Xbox Mode shell transitions GetForegroundWindow can briefly be null.
                return true;
            }

            if (fg == _windowHandle || IsChild(_windowHandle, fg))
            {
                return true;
            }

            IntPtr root = GetAncestor(fg, GaRoot);
            if (root == _windowHandle)
            {
                return true;
            }

            uint fgPid;
            GetWindowThreadProcessId(fg, out fgPid);
            if (fgPid == _currentProcessId)
            {
                return true;
            }

            if (_webViewBrowserProcessId != 0 && fgPid == _webViewBrowserProcessId)
            {
                return true;
            }

            return false;
        }

        private int ReadNativeControllers(out ControllerReading combined)
        {
            _readingScratch.Clear();

            // 1. XInput (slots 0..3)
            int xInputCount = 0;
            if (!_xInputUnavailable)
            {
                for (uint i = 0; i < MaxXInputControllers; i++)
                {
                    XInputState state;
                    if (TryGetXInputState(i, out state))
                    {
                        xInputCount++;
                        _readingScratch.Add(ConvertXInputReading(state.Gamepad));
                    }
                }
            }

            // 2. Windows.Gaming.Input.Gamepad (GameInput-backed — native in Windows 11 Xbox Mode)
            int wgiCount = 0;
            if (_wgiAvailable)
            {
                try
                {
                    IReadOnlyList<Gamepad> currentPads = Gamepad.Gamepads;
                    for (int i = 0; i < currentPads.Count; i++)
                    {
                        GamepadReading raw = currentPads[i].GetCurrentReading();
                        wgiCount++;
                        _readingScratch.Add(ConvertWgiReading(raw));
                    }
                }
                catch (Exception)
                {
                    // Ignore transient WinRT read exceptions if a controller disconnects mid-frame.
                }
            }

            combined = ControllerInputProcessor.CombineReadings(_readingScratch);
            return xInputCount > wgiCount ? xInputCount : wgiCount;
        }

        private bool TryGetXInputState(uint userIndex, out XInputState state)
        {
            state = default(XInputState);
            if (_xInputUnavailable)
            {
                return false;
            }

            try
            {
                uint result = _useXInput910Fallback
                    ? XInputGetState910(userIndex, out state)
                    : XInputGetState14(userIndex, out state);
                return result == ErrorSuccess;
            }
            catch (DllNotFoundException)
            {
                if (!_useXInput910Fallback)
                {
                    _useXInput910Fallback = true;
                    return TryGetXInputState(userIndex, out state);
                }
                _xInputUnavailable = true;
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                _xInputUnavailable = true;
                return false;
            }
        }

        private static ControllerReading ConvertXInputReading(XInputGamepad pad)
        {
            ControllerButton buttons = ControllerButton.None;
            ushort w = pad.wButtons;

            if ((w & XInputStart) != 0) buttons |= ControllerButton.Menu;
            if ((w & XInputBack) != 0) buttons |= ControllerButton.View;
            if ((w & XInputA) != 0) buttons |= ControllerButton.A;
            if ((w & XInputB) != 0) buttons |= ControllerButton.B;
            if ((w & XInputX) != 0) buttons |= ControllerButton.X;
            if ((w & XInputY) != 0) buttons |= ControllerButton.Y;
            if ((w & XInputDPadUp) != 0) buttons |= ControllerButton.DPadUp;
            if ((w & XInputDPadDown) != 0) buttons |= ControllerButton.DPadDown;
            if ((w & XInputDPadLeft) != 0) buttons |= ControllerButton.DPadLeft;
            if ((w & XInputDPadRight) != 0) buttons |= ControllerButton.DPadRight;
            if ((w & XInputLeftShoulder) != 0) buttons |= ControllerButton.LeftShoulder;
            if ((w & XInputRightShoulder) != 0) buttons |= ControllerButton.RightShoulder;
            if ((w & XInputLeftThumb) != 0) buttons |= ControllerButton.LeftThumbstick;
            if ((w & XInputRightThumb) != 0) buttons |= ControllerButton.RightThumbstick;

            return new ControllerReading
            {
                Buttons = buttons,
                LeftThumbstickX = NormalizeThumbstick(pad.sThumbLX),
                LeftThumbstickY = NormalizeThumbstick(pad.sThumbLY),
                RightThumbstickX = NormalizeThumbstick(pad.sThumbRX),
                RightThumbstickY = NormalizeThumbstick(pad.sThumbRY),
                LeftTrigger = pad.bLeftTrigger / 255.0,
                RightTrigger = pad.bRightTrigger / 255.0
            };
        }

        private static ControllerReading ConvertWgiReading(GamepadReading raw)
        {
            ControllerButton buttons = ControllerButton.None;
            GamepadButtons b = raw.Buttons;

            if ((b & GamepadButtons.Menu) != 0) buttons |= ControllerButton.Menu;
            if ((b & GamepadButtons.View) != 0) buttons |= ControllerButton.View;
            if ((b & GamepadButtons.A) != 0) buttons |= ControllerButton.A;
            if ((b & GamepadButtons.B) != 0) buttons |= ControllerButton.B;
            if ((b & GamepadButtons.X) != 0) buttons |= ControllerButton.X;
            if ((b & GamepadButtons.Y) != 0) buttons |= ControllerButton.Y;
            if ((b & GamepadButtons.DPadUp) != 0) buttons |= ControllerButton.DPadUp;
            if ((b & GamepadButtons.DPadDown) != 0) buttons |= ControllerButton.DPadDown;
            if ((b & GamepadButtons.DPadLeft) != 0) buttons |= ControllerButton.DPadLeft;
            if ((b & GamepadButtons.DPadRight) != 0) buttons |= ControllerButton.DPadRight;
            if ((b & GamepadButtons.LeftShoulder) != 0) buttons |= ControllerButton.LeftShoulder;
            if ((b & GamepadButtons.RightShoulder) != 0) buttons |= ControllerButton.RightShoulder;
            if ((b & GamepadButtons.LeftThumbstick) != 0) buttons |= ControllerButton.LeftThumbstick;
            if ((b & GamepadButtons.RightThumbstick) != 0) buttons |= ControllerButton.RightThumbstick;

            return new ControllerReading
            {
                Buttons = buttons,
                LeftThumbstickX = raw.LeftThumbstickX,
                LeftThumbstickY = raw.LeftThumbstickY,
                RightThumbstickX = raw.RightThumbstickX,
                RightThumbstickY = raw.RightThumbstickY,
                LeftTrigger = raw.LeftTrigger,
                RightTrigger = raw.RightTrigger
            };
        }

        private static double NormalizeThumbstick(short value)
        {
            double normalized = value / 32767.0;
            if (normalized < -1.0) return -1.0;
            if (normalized > 1.0) return 1.0;
            return normalized;
        }

        private void SetWebFallbackSuppressed(bool suppressed)
        {
            if (_nativeInputVerifiedInSession == suppressed)
            {
                return;
            }

            _nativeInputVerifiedInSession = suppressed;
            EventHandler<bool> handler = WebFallbackSuppressedChanged;
            if (handler != null)
            {
                handler(this, suppressed);
            }
        }

        private void UpdatePollingTimer()
        {
            if (_isDisposed)
            {
                if (_pollTimer.IsEnabled)
                {
                    _pollTimer.Stop();
                }
                return;
            }

            TimeSpan desiredInterval;
            if (!_isForeground)
            {
                desiredInterval = BackgroundCheckInterval;
            }
            else if (_nativeGamepadCount > 0 || _hasActiveWebInput || _processor.HasHeldActions)
            {
                desiredInterval = ActivePollInterval;
            }
            else
            {
                desiredInterval = IdleScanInterval;
            }

            if (_pollTimer.Interval != desiredInterval)
            {
                _pollTimer.Interval = desiredInterval;
            }

            if (!_pollTimer.IsEnabled)
            {
                _pollTimer.Start();
            }
        }

        private void EmitEvents(List<ControllerActionEvent> events)
        {
            EventHandler<ControllerActionEvent> handler = ActionTriggered;
            if (handler == null || events == null)
            {
                return;
            }

            for (int i = 0; i < events.Count; i++)
            {
                handler(this, events[i]);
            }
        }

        private void RaiseConnectionToast(bool connected)
        {
            EventHandler<bool> handler = ControllerConnectionToastRequested;
            if (handler != null)
            {
                handler(this, connected);
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            if (_wgiAvailable)
            {
                try
                {
                    Gamepad.GamepadAdded -= WgiGamepad_GamepadAdded;
                    Gamepad.GamepadRemoved -= WgiGamepad_GamepadRemoved;
                }
                catch (Exception)
                {
                }
            }
            _pollTimer.Stop();
            _pollTimer.Tick -= PollTimer_Tick;
            _processor.Reset();
        }
    }
}
