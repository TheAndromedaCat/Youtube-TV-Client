using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Youtube_TV.Input;

namespace Youtube_TV.Desktop.Input
{
    /// <summary>
    /// Coordinates native XInput controller polling (Xbox controllers, ASUS ROG Ally,
    /// Lenovo Legion Go, Steam Deck on Windows, XInput gamepads) alongside an automatic
    /// W3C Web Gamepad API fallback for direct USB/Bluetooth PlayStation and Switch controllers.
    /// </summary>
    public sealed class DesktopGamepadService : IDisposable
    {
        private const int MaxXInputControllers = 4;
        private const uint ErrorSuccess = 0;

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
        private static readonly TimeSpan IdleScanInterval = TimeSpan.FromMilliseconds(1000);

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

        private readonly ControllerInputProcessor _processor = new ControllerInputProcessor();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly DispatcherTimer _pollTimer;
        private readonly List<ControllerReading> _readingScratch = new List<ControllerReading>(MaxXInputControllers);

        private bool _useXInput910Fallback;
        private bool _xInputUnavailable;
        private bool _isWindowActive = true;
        private bool _isDisposed;
        private int _nativeGamepadCount;
        private ControllerReading _webReading;
        private bool _hasActiveWebInput;

        public event EventHandler<ControllerActionEvent> ActionTriggered;
        public event EventHandler<int> NativeGamepadCountChanged;
        public event EventHandler<bool> ControllerConnectionToastRequested;
        public event EventHandler FullscreenToggleRequested;
        public event EventHandler HelpOverlayDismissedFromWeb;

        public DesktopGamepadService(Dispatcher dispatcher)
        {
            if (dispatcher == null)
            {
                throw new ArgumentNullException(nameof(dispatcher));
            }

            _pollTimer = new DispatcherTimer(DispatcherPriority.Input, dispatcher);
            _pollTimer.Tick += PollTimer_Tick;

            // Detect already-connected controllers on startup without firing a redundant toast.
            ControllerReading initialReading;
            _nativeGamepadCount = ReadXInputControllers(out initialReading);
            UpdatePollingTimer();
        }

        public int NativeGamepadCount
        {
            get { return _nativeGamepadCount; }
        }

        /// <summary>
        /// Script injected into WebView2 on document creation to provide:
        /// 1. W3C Web Gamepad API fallback (DualSense / DualShock 4 / Switch Pro) when no XInput controller is connected.
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

    var nativeCount = 0;
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

    window.__ytTvSetNativeGamepadCount = function(count) {
        nativeCount = count || 0;
        if (nativeCount > 0 && lastPayload !== '') {
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
        if (nativeCount > 0 || !hasWebGamepads()) {
            return;
        }

        if (document.hasFocus()) {
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
        }

        rafId = window.requestAnimationFrame(pollWebGamepads);
    }

    function updateLoop() {
        if (nativeCount === 0 && hasWebGamepads()) {
            if (!rafId) {
                rafId = window.requestAnimationFrame(pollWebGamepads);
            }
        } else if (rafId) {
            window.cancelAnimationFrame(rafId);
            rafId = 0;
        }
    }

    window.addEventListener('gamepadconnected', function() {
        if (nativeCount === 0) {
            post('gp_connected');
        }
        updateLoop();
    });

    window.addEventListener('gamepaddisconnected', function() {
        if (nativeCount === 0 && !hasWebGamepads()) {
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

        public void SetWindowActive(bool isActive)
        {
            if (_isDisposed || _isWindowActive == isActive)
            {
                return;
            }

            _isWindowActive = isActive;
            if (!_isWindowActive)
            {
                _webReading = new ControllerReading();
                _hasActiveWebInput = false;
                EmitEvents(_processor.Reset());
            }

            UpdatePollingTimer();
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
                if (_nativeGamepadCount == 0)
                {
                    EmitEvents(_processor.Reset());
                    RaiseConnectionToast(false);
                }
                UpdatePollingTimer();
                return;
            }

            if (!_isWindowActive || _nativeGamepadCount > 0)
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

        private void PollTimer_Tick(object sender, EventArgs e)
        {
            if (_isDisposed || !_isWindowActive)
            {
                return;
            }

            ControllerReading combined;
            int count = ReadXInputControllers(out combined);

            if (count != _nativeGamepadCount)
            {
                int previous = _nativeGamepadCount;
                _nativeGamepadCount = count;

                if (count > 0 && previous == 0)
                {
                    _webReading = new ControllerReading();
                    _hasActiveWebInput = false;
                    RaiseNativeCountChanged(count);
                    RaiseConnectionToast(true);
                }
                else if (count == 0 && previous > 0)
                {
                    EmitEvents(_processor.Reset());
                    RaiseNativeCountChanged(0);
                    RaiseConnectionToast(false);
                }
                else
                {
                    RaiseNativeCountChanged(count);
                }

                UpdatePollingTimer();
            }

            if (count > 0)
            {
                EmitEvents(_processor.Update(combined, _clock.ElapsedMilliseconds));
            }
            else if (_hasActiveWebInput || _processor.HasHeldActions)
            {
                EmitEvents(_processor.Update(_webReading, _clock.ElapsedMilliseconds));
                if (!_hasActiveWebInput && !_processor.HasHeldActions)
                {
                    UpdatePollingTimer();
                }
            }
        }

        private int ReadXInputControllers(out ControllerReading combined)
        {
            _readingScratch.Clear();
            if (_xInputUnavailable)
            {
                combined = new ControllerReading();
                return 0;
            }

            for (uint i = 0; i < MaxXInputControllers; i++)
            {
                XInputState state;
                if (TryGetXInputState(i, out state))
                {
                    _readingScratch.Add(ConvertXInputReading(state.Gamepad));
                }
            }

            combined = ControllerInputProcessor.CombineReadings(_readingScratch);
            return _readingScratch.Count;
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

        private static double NormalizeThumbstick(short value)
        {
            double normalized = value / 32767.0;
            if (normalized < -1.0) return -1.0;
            if (normalized > 1.0) return 1.0;
            return normalized;
        }

        private void UpdatePollingTimer()
        {
            if (_isDisposed || !_isWindowActive)
            {
                if (_pollTimer.IsEnabled)
                {
                    _pollTimer.Stop();
                }
                return;
            }

            bool fastPoll = _nativeGamepadCount > 0 || _hasActiveWebInput || _processor.HasHeldActions;
            TimeSpan desiredInterval = fastPoll ? ActivePollInterval : IdleScanInterval;
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

        private void RaiseNativeCountChanged(int count)
        {
            EventHandler<int> handler = NativeGamepadCountChanged;
            if (handler != null)
            {
                handler(this, count);
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
            _pollTimer.Stop();
            _pollTimer.Tick -= PollTimer_Tick;
            _processor.Reset();
        }
    }
}
