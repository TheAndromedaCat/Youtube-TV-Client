using System;
using System.Collections.Generic;
using System.Globalization;

namespace Youtube_TV.Input
{
    [Flags]
    public enum ControllerButton
    {
        None = 0,
        Menu = 1 << 0,
        View = 1 << 1,
        A = 1 << 2,
        B = 1 << 3,
        X = 1 << 4,
        Y = 1 << 5,
        DPadUp = 1 << 6,
        DPadDown = 1 << 7,
        DPadLeft = 1 << 8,
        DPadRight = 1 << 9,
        LeftShoulder = 1 << 10,
        RightShoulder = 1 << 11,
        LeftThumbstick = 1 << 12,
        RightThumbstick = 1 << 13
    }

    public struct ControllerReading
    {
        public ControllerButton Buttons;
        public double LeftThumbstickX;
        public double LeftThumbstickY;
        public double RightThumbstickX;
        public double RightThumbstickY;
        public double LeftTrigger;
        public double RightTrigger;
    }

    public enum ControllerAction
    {
        NavigateUp,
        NavigateDown,
        NavigateLeft,
        NavigateRight,
        Select,
        Back,
        PlayPause,
        Search,
        Rewind,
        FastForward,
        PreviousVideo,
        NextVideo,
        VolumeUp,
        VolumeDown,
        ToggleMute,
        ToggleFullscreen,
        ToggleControlsHelp
    }

    public struct ControllerActionEvent
    {
        public ControllerAction Action;
        public bool IsPressed;
        public bool IsRepeat;

        public ControllerActionEvent(ControllerAction action, bool isPressed, bool isRepeat)
        {
            Action = action;
            IsPressed = isPressed;
            IsRepeat = isRepeat;
        }
    }

    public struct ControllerKeySpec
    {
        public string Key;
        public string Code;
        public int VirtualKeyCode;

        public ControllerKeySpec(string key, string code, int virtualKeyCode)
        {
            Key = key;
            Code = code;
            VirtualKeyCode = virtualKeyCode;
        }

        public string ToCdpKeyEventJson(bool isKeyDown, bool autoRepeat)
        {
            string eventType = isKeyDown ? "rawKeyDown" : "keyUp";
            string repeatFlag = autoRepeat ? "true" : "false";
            return string.Format(
                CultureInfo.InvariantCulture,
                "{{\"type\":\"{0}\",\"key\":\"{1}\",\"code\":\"{2}\",\"windowsVirtualKeyCode\":{3},\"nativeVirtualKeyCode\":{3},\"autoRepeat\":{4}}}",
                eventType,
                Key,
                Code,
                VirtualKeyCode,
                repeatFlag);
        }
    }

    /// <summary>
    /// Converts raw controller readings into debounced, repeatable YouTube TV actions.
    /// Designed to be platform-agnostic so the same input pipeline serves native Windows.Gaming.Input
    /// controllers (Xbox, ROG Ally, Legion Go, XInput) and W3C Web Gamepad fallback controllers
    /// (PlayStation DualSense/DualShock, Switch Pro).
    /// </summary>
    public sealed class ControllerInputProcessor
    {
        public const double DefaultStickDeadzone = 0.35;
        public const double DefaultTriggerThreshold = 0.40;

        public const long NavigationRepeatDelayMs = 350;
        public const long NavigationRepeatIntervalMs = 110;
        public const long SeekRepeatDelayMs = 260;
        public const long SeekRepeatIntervalMs = 95;
        public const long VolumeRepeatDelayMs = 250;
        public const long VolumeRepeatIntervalMs = 100;

        private readonly double _stickDeadzone;
        private readonly double _triggerThreshold;
        private readonly Dictionary<ControllerAction, long> _heldActionNextRepeatMs = new Dictionary<ControllerAction, long>();
        private readonly HashSet<ControllerAction> _activeScratch = new HashSet<ControllerAction>();
        private readonly List<ControllerAction> _releasedScratch = new List<ControllerAction>();

        public ControllerInputProcessor(
            double stickDeadzone = DefaultStickDeadzone,
            double triggerThreshold = DefaultTriggerThreshold)
        {
            _stickDeadzone = stickDeadzone;
            _triggerThreshold = triggerThreshold;
        }

        public bool HasHeldActions
        {
            get { return _heldActionNextRepeatMs.Count > 0; }
        }

        /// <summary>
        /// Merges multiple connected controllers into a single combined reading so devices like
        /// a docked ROG Ally and a wireless couch controller can both drive the app seamlessly.
        /// </summary>
        public static ControllerReading CombineReadings(IReadOnlyList<ControllerReading> readings)
        {
            ControllerReading combined = new ControllerReading();
            if (readings == null || readings.Count == 0)
            {
                return combined;
            }

            double bestLeftMagSq = 0.0;
            double bestRightMagSq = 0.0;

            for (int i = 0; i < readings.Count; i++)
            {
                ControllerReading r = readings[i];
                combined.Buttons |= r.Buttons;

                double leftMagSq = (r.LeftThumbstickX * r.LeftThumbstickX) + (r.LeftThumbstickY * r.LeftThumbstickY);
                if (leftMagSq > bestLeftMagSq)
                {
                    bestLeftMagSq = leftMagSq;
                    combined.LeftThumbstickX = r.LeftThumbstickX;
                    combined.LeftThumbstickY = r.LeftThumbstickY;
                }

                double rightMagSq = (r.RightThumbstickX * r.RightThumbstickX) + (r.RightThumbstickY * r.RightThumbstickY);
                if (rightMagSq > bestRightMagSq)
                {
                    bestRightMagSq = rightMagSq;
                    combined.RightThumbstickX = r.RightThumbstickX;
                    combined.RightThumbstickY = r.RightThumbstickY;
                }

                if (r.LeftTrigger > combined.LeftTrigger)
                {
                    combined.LeftTrigger = r.LeftTrigger;
                }

                if (r.RightTrigger > combined.RightTrigger)
                {
                    combined.RightTrigger = r.RightTrigger;
                }
            }

            return combined;
        }

        /// <summary>
        /// Updates held/repeat state for the latest controller reading and returns any
        /// press, auto-repeat, or release events to dispatch.
        /// </summary>
        public List<ControllerActionEvent> Update(ControllerReading reading, long nowMs)
        {
            List<ControllerActionEvent> events = new List<ControllerActionEvent>();
            _activeScratch.Clear();
            ResolveActiveActions(reading, _activeScratch);

            // Emit releases for actions that are no longer active.
            _releasedScratch.Clear();
            foreach (KeyValuePair<ControllerAction, long> pair in _heldActionNextRepeatMs)
            {
                if (!_activeScratch.Contains(pair.Key))
                {
                    _releasedScratch.Add(pair.Key);
                }
            }

            for (int i = 0; i < _releasedScratch.Count; i++)
            {
                ControllerAction released = _releasedScratch[i];
                _heldActionNextRepeatMs.Remove(released);
                events.Add(new ControllerActionEvent(released, false, false));
            }

            // Emit initial presses and auto-repeats for active actions.
            foreach (ControllerAction action in _activeScratch)
            {
                long nextRepeatMs;
                if (!_heldActionNextRepeatMs.TryGetValue(action, out nextRepeatMs))
                {
                    long delayMs;
                    long intervalMs;
                    long scheduledRepeat = TryGetRepeatTimings(action, out delayMs, out intervalMs)
                        ? nowMs + delayMs
                        : long.MaxValue;

                    _heldActionNextRepeatMs[action] = scheduledRepeat;
                    events.Add(new ControllerActionEvent(action, true, false));
                }
                else if (nowMs >= nextRepeatMs)
                {
                    long delayMs;
                    long intervalMs;
                    if (TryGetRepeatTimings(action, out delayMs, out intervalMs))
                    {
                        _heldActionNextRepeatMs[action] = nowMs + intervalMs;
                        events.Add(new ControllerActionEvent(action, true, true));
                    }
                }
            }

            return events;
        }

        /// <summary>
        /// Releases all currently held actions. Call when the app loses focus or all controllers disconnect.
        /// </summary>
        public List<ControllerActionEvent> Reset()
        {
            List<ControllerActionEvent> events = new List<ControllerActionEvent>();
            if (_heldActionNextRepeatMs.Count == 0)
            {
                return events;
            }

            foreach (KeyValuePair<ControllerAction, long> pair in _heldActionNextRepeatMs)
            {
                events.Add(new ControllerActionEvent(pair.Key, false, false));
            }

            _heldActionNextRepeatMs.Clear();
            return events;
        }

        public static bool TryGetKeySpec(ControllerAction action, out ControllerKeySpec spec)
        {
            switch (action)
            {
                case ControllerAction.NavigateUp:
                    spec = new ControllerKeySpec("ArrowUp", "ArrowUp", 38);
                    return true;
                case ControllerAction.NavigateDown:
                    spec = new ControllerKeySpec("ArrowDown", "ArrowDown", 40);
                    return true;
                case ControllerAction.NavigateLeft:
                case ControllerAction.Rewind:
                    spec = new ControllerKeySpec("ArrowLeft", "ArrowLeft", 37);
                    return true;
                case ControllerAction.NavigateRight:
                case ControllerAction.FastForward:
                    spec = new ControllerKeySpec("ArrowRight", "ArrowRight", 39);
                    return true;
                case ControllerAction.Select:
                    spec = new ControllerKeySpec("Enter", "Enter", 13);
                    return true;
                case ControllerAction.Back:
                    spec = new ControllerKeySpec("Escape", "Escape", 27);
                    return true;
                case ControllerAction.PlayPause:
                    spec = new ControllerKeySpec("MediaPlayPause", "MediaPlayPause", 179);
                    return true;
                case ControllerAction.Search:
                    spec = new ControllerKeySpec("s", "KeyS", 83);
                    return true;
                case ControllerAction.PreviousVideo:
                    spec = new ControllerKeySpec("MediaTrackPrevious", "MediaTrackPrevious", 177);
                    return true;
                case ControllerAction.NextVideo:
                    spec = new ControllerKeySpec("MediaTrackNext", "MediaTrackNext", 176);
                    return true;
                default:
                    spec = default(ControllerKeySpec);
                    return false;
            }
        }

        /// <summary>
        /// Parses a compact web-gamepad bridge message ("gp:mask|lx|ly|rx|ry|lt|rt") posted
        /// by the WebView2 fallback script for DirectInput/HID controllers such as DualSense.
        /// </summary>
        public static bool TryParseWebGamepadMessage(string message, out ControllerReading reading)
        {
            reading = new ControllerReading();
            if (string.IsNullOrEmpty(message) || !message.StartsWith("gp:", StringComparison.Ordinal))
            {
                return false;
            }

            string payload = message.Substring(3);
            string[] parts = payload.Split('|');
            if (parts.Length != 7)
            {
                return false;
            }

            int buttonMask;
            double lx;
            double ly;
            double rx;
            double ry;
            double lt;
            double rt;

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out buttonMask) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lx) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out ly) ||
                !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out rx) ||
                !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out ry) ||
                !double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out lt) ||
                !double.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out rt))
            {
                return false;
            }

            reading.Buttons = (ControllerButton)buttonMask;
            reading.LeftThumbstickX = Clamp(lx, -1.0, 1.0);
            reading.LeftThumbstickY = Clamp(ly, -1.0, 1.0);
            reading.RightThumbstickX = Clamp(rx, -1.0, 1.0);
            reading.RightThumbstickY = Clamp(ry, -1.0, 1.0);
            reading.LeftTrigger = Clamp(lt, 0.0, 1.0);
            reading.RightTrigger = Clamp(rt, 0.0, 1.0);
            return true;
        }

        public static bool IsNeutral(ControllerReading reading, double stickDeadzone = DefaultStickDeadzone, double triggerThreshold = DefaultTriggerThreshold)
        {
            if (reading.Buttons != ControllerButton.None)
            {
                return false;
            }

            double deadzoneSq = stickDeadzone * stickDeadzone;
            double leftMagSq = (reading.LeftThumbstickX * reading.LeftThumbstickX) + (reading.LeftThumbstickY * reading.LeftThumbstickY);
            if (leftMagSq >= deadzoneSq)
            {
                return false;
            }

            double rightMagSq = (reading.RightThumbstickX * reading.RightThumbstickX) + (reading.RightThumbstickY * reading.RightThumbstickY);
            if (rightMagSq >= deadzoneSq)
            {
                return false;
            }

            return reading.LeftTrigger < triggerThreshold && reading.RightTrigger < triggerThreshold;
        }

        private void ResolveActiveActions(ControllerReading reading, HashSet<ControllerAction> target)
        {
            ControllerButton b = reading.Buttons;

            // D-Pad navigation
            if ((b & ControllerButton.DPadUp) != 0) target.Add(ControllerAction.NavigateUp);
            if ((b & ControllerButton.DPadDown) != 0) target.Add(ControllerAction.NavigateDown);
            if ((b & ControllerButton.DPadLeft) != 0) target.Add(ControllerAction.NavigateLeft);
            if ((b & ControllerButton.DPadRight) != 0) target.Add(ControllerAction.NavigateRight);

            // Left stick navigation with radial deadzone and cardinal dominant-axis disambiguation
            double deadzoneSq = _stickDeadzone * _stickDeadzone;
            double leftMagSq = (reading.LeftThumbstickX * reading.LeftThumbstickX) + (reading.LeftThumbstickY * reading.LeftThumbstickY);
            if (leftMagSq >= deadzoneSq)
            {
                if (Math.Abs(reading.LeftThumbstickX) >= Math.Abs(reading.LeftThumbstickY))
                {
                    target.Add(reading.LeftThumbstickX > 0 ? ControllerAction.NavigateRight : ControllerAction.NavigateLeft);
                }
                else
                {
                    // In Windows.Gaming.Input (and normalized web readings), +Y is Up.
                    target.Add(reading.LeftThumbstickY > 0 ? ControllerAction.NavigateUp : ControllerAction.NavigateDown);
                }
            }

            // Right stick vertical volume control (dominant vertical axis only)
            double rightMagSq = (reading.RightThumbstickX * reading.RightThumbstickX) + (reading.RightThumbstickY * reading.RightThumbstickY);
            if (rightMagSq >= deadzoneSq && Math.Abs(reading.RightThumbstickY) > Math.Abs(reading.RightThumbstickX))
            {
                target.Add(reading.RightThumbstickY > 0 ? ControllerAction.VolumeUp : ControllerAction.VolumeDown);
            }

            // Face buttons
            if ((b & ControllerButton.A) != 0) target.Add(ControllerAction.Select);
            if ((b & ControllerButton.B) != 0) target.Add(ControllerAction.Back);
            if ((b & ControllerButton.X) != 0 || (b & ControllerButton.Menu) != 0) target.Add(ControllerAction.PlayPause);
            if ((b & ControllerButton.Y) != 0) target.Add(ControllerAction.Search);

            // Bumpers: previous / next video
            if ((b & ControllerButton.LeftShoulder) != 0) target.Add(ControllerAction.PreviousVideo);
            if ((b & ControllerButton.RightShoulder) != 0) target.Add(ControllerAction.NextVideo);

            // Triggers: fast rewind / fast forward
            if (reading.LeftTrigger >= _triggerThreshold) target.Add(ControllerAction.Rewind);
            if (reading.RightTrigger >= _triggerThreshold) target.Add(ControllerAction.FastForward);

            // System & stick-click actions
            if ((b & ControllerButton.View) != 0) target.Add(ControllerAction.ToggleFullscreen);
            if ((b & ControllerButton.RightThumbstick) != 0) target.Add(ControllerAction.ToggleMute);
            if ((b & ControllerButton.LeftThumbstick) != 0) target.Add(ControllerAction.ToggleControlsHelp);
        }

        private static bool TryGetRepeatTimings(ControllerAction action, out long delayMs, out long intervalMs)
        {
            switch (action)
            {
                case ControllerAction.NavigateUp:
                case ControllerAction.NavigateDown:
                case ControllerAction.NavigateLeft:
                case ControllerAction.NavigateRight:
                    delayMs = NavigationRepeatDelayMs;
                    intervalMs = NavigationRepeatIntervalMs;
                    return true;
                case ControllerAction.Rewind:
                case ControllerAction.FastForward:
                    delayMs = SeekRepeatDelayMs;
                    intervalMs = SeekRepeatIntervalMs;
                    return true;
                case ControllerAction.VolumeUp:
                case ControllerAction.VolumeDown:
                    delayMs = VolumeRepeatDelayMs;
                    intervalMs = VolumeRepeatIntervalMs;
                    return true;
                default:
                    delayMs = 0;
                    intervalMs = 0;
                    return false;
            }
        }

        private static double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return 0.0;
            }

            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
