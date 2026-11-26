using System;
using System.Collections.Generic;
using System.Diagnostics;
using Windows.Gaming.Input;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace Youtube_TV.Input
{
    /// <summary>
    /// Coordinates native UWP Windows.Gaming.Input.Gamepad polling (Xbox controllers, ASUS ROG Ally,
    /// Lenovo Legion Go, XInput gamepads) alongside an automatic W3C Web Gamepad bridge fallback
    /// for DirectInput/HID controllers (PlayStation DualSense/DualShock, Switch Pro).
    /// </summary>
    public sealed class GamepadService : IDisposable
    {
        private readonly CoreDispatcher _dispatcher;
        private readonly ControllerInputProcessor _processor = new ControllerInputProcessor();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly DispatcherTimer _pollTimer;
        private readonly object _gamepadLock = new object();
        private readonly List<Gamepad> _gamepads = new List<Gamepad>();
        private readonly List<ControllerReading> _readingScratch = new List<ControllerReading>();

        private bool _isWindowActive = true;
        private bool _isDisposed;
        private ControllerReading _webReading;
        private bool _hasActiveWebInput;

        public event EventHandler<ControllerActionEvent> ActionTriggered;
        public event EventHandler<int> NativeGamepadCountChanged;
        public event EventHandler<bool> ControllerConnectionToastRequested;

        public GamepadService(CoreDispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

            _pollTimer = new DispatcherTimer();
            _pollTimer.Interval = TimeSpan.FromMilliseconds(16);
            _pollTimer.Tick += PollTimer_Tick;

            lock (_gamepadLock)
            {
                IReadOnlyList<Gamepad> existing = Gamepad.Gamepads;
                for (int i = 0; i < existing.Count; i++)
                {
                    if (!_gamepads.Contains(existing[i]))
                    {
                        _gamepads.Add(existing[i]);
                    }
                }
            }

            Gamepad.GamepadAdded += Gamepad_GamepadAdded;
            Gamepad.GamepadRemoved += Gamepad_GamepadRemoved;

            UpdatePollingState();
        }

        public int NativeGamepadCount
        {
            get
            {
                lock (_gamepadLock)
                {
                    return _gamepads.Count;
                }
            }
        }

        /// <summary>
        /// Script injected into WebView2 on document creation to:
        /// 1. Provide a fallback W3C Gamepad API bridge for controllers not exposed via Windows.Gaming.Input
        ///    (e.g., PlayStation DualSense / DualShock 4 / Switch Pro connected directly over USB or Bluetooth).
        ///    This fallback automatically stays idle whenever any native UWP gamepad is active.
        /// 2. Provide consistent HTML5 video volume control across YouTube TV autoplay transitions.
        /// </summary>
        public static string WebGamepadBridgeScript
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

    function post(msg) {
        try {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(msg);
            }
        } catch (err) { }
    }

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

            UpdatePollingState();
        }

        public void HandleWebMessage(string message)
        {
            if (_isDisposed || string.IsNullOrEmpty(message))
            {
                return;
            }

            if (message == "gp_connected")
            {
                if (NativeGamepadCount == 0)
                {
                    RaiseConnectionToast(true);
                }
                return;
            }

            if (message == "gp_disconnected")
            {
                _webReading = new ControllerReading();
                _hasActiveWebInput = false;
                if (NativeGamepadCount == 0)
                {
                    EmitEvents(_processor.Reset());
                    RaiseConnectionToast(false);
                }
                UpdatePollingState();
                return;
            }

            if (!_isWindowActive || NativeGamepadCount > 0)
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

            // Process immediately for lowest latency, then let the timer drive hold-to-repeat.
            EmitEvents(_processor.Update(_webReading, _clock.ElapsedMilliseconds));
            UpdatePollingState();
        }

        private void Gamepad_GamepadAdded(object sender, Gamepad gamepad)
        {
            if (_isDisposed || gamepad == null)
            {
                return;
            }

            int count;
            lock (_gamepadLock)
            {
                if (!_gamepads.Contains(gamepad))
                {
                    _gamepads.Add(gamepad);
                }
                count = _gamepads.Count;
            }

            var ignore = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (_isDisposed) return;
                _webReading = new ControllerReading();
                _hasActiveWebInput = false;
                UpdatePollingState();
                RaiseNativeCountChanged(count);
                RaiseConnectionToast(true);
            });
        }

        private void Gamepad_GamepadRemoved(object sender, Gamepad gamepad)
        {
            if (_isDisposed || gamepad == null)
            {
                return;
            }

            int count;
            lock (_gamepadLock)
            {
                _gamepads.Remove(gamepad);
                count = _gamepads.Count;
            }

            var ignore = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (_isDisposed) return;
                if (count == 0)
                {
                    EmitEvents(_processor.Reset());
                }
                UpdatePollingState();
                RaiseNativeCountChanged(count);
                RaiseConnectionToast(false);
            });
        }

        private void PollTimer_Tick(object sender, object e)
        {
            if (_isDisposed || !_isWindowActive)
            {
                return;
            }

            ControllerReading combined;
            int nativeCount = ReadNativeGamepads(out combined);

            if (nativeCount > 0)
            {
                EmitEvents(_processor.Update(combined, _clock.ElapsedMilliseconds));
            }
            else if (_hasActiveWebInput || _processor.HasHeldActions)
            {
                EmitEvents(_processor.Update(_webReading, _clock.ElapsedMilliseconds));
                if (!_hasActiveWebInput && !_processor.HasHeldActions)
                {
                    UpdatePollingState();
                }
            }
            else
            {
                UpdatePollingState();
            }
        }

        private int ReadNativeGamepads(out ControllerReading combined)
        {
            _readingScratch.Clear();
            lock (_gamepadLock)
            {
                for (int i = 0; i < _gamepads.Count; i++)
                {
                    GamepadReading raw = _gamepads[i].GetCurrentReading();
                    _readingScratch.Add(ConvertReading(raw));
                }
            }

            combined = ControllerInputProcessor.CombineReadings(_readingScratch);
            return _readingScratch.Count;
        }

        private static ControllerReading ConvertReading(GamepadReading raw)
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

        private void UpdatePollingState()
        {
            bool shouldPoll = _isWindowActive && (NativeGamepadCount > 0 || _hasActiveWebInput || _processor.HasHeldActions);
            if (shouldPoll && !_pollTimer.IsEnabled)
            {
                _pollTimer.Start();
            }
            else if (!shouldPoll && _pollTimer.IsEnabled)
            {
                _pollTimer.Stop();
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
            Gamepad.GamepadAdded -= Gamepad_GamepadAdded;
            Gamepad.GamepadRemoved -= Gamepad_GamepadRemoved;
            _pollTimer.Stop();
            _pollTimer.Tick -= PollTimer_Tick;
            _processor.Reset();
        }
    }
}
