using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Input.Preview.Injection;
using Windows.UI.ViewManagement;
using Windows.UI.WebUI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Youtube_TV.Input;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace Youtube_TV
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private const long BackDeduplicationWindowMs = 250;

        SystemNavigationManager currentView;
        private readonly GamepadService _gamepadService;
        private readonly DispatcherTimer _toastTimer;
        private readonly Stopwatch _eventClock = Stopwatch.StartNew();
        private readonly Dictionary<int, int> _heldVirtualKeyCounts = new Dictionary<int, int>();

        private long _lastGamepadBackMs = -BackDeduplicationWindowMs;
        private bool _hasShownInitialControllerToast;

        public const uint GW_CHILD = 5;
        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr hWnd);


        public MainPage()
        {
            this.InitializeComponent();
            currentView = SystemNavigationManager.GetForCurrentView();
            currentView.AppViewBackButtonVisibility = AppViewBackButtonVisibility.Collapsed;
            currentView.BackRequested += CurrentView_BackRequested;

            _toastTimer = new DispatcherTimer();
            _toastTimer.Interval = TimeSpan.FromMilliseconds(2200);
            _toastTimer.Tick += ToastTimer_Tick;

            _gamepadService = new GamepadService(Dispatcher);
            _gamepadService.ActionTriggered += GamepadService_ActionTriggered;
            _gamepadService.NativeGamepadCountChanged += GamepadService_NativeGamepadCountChanged;
            _gamepadService.ControllerConnectionToastRequested += GamepadService_ControllerConnectionToastRequested;

            Window.Current.Activated += CurrentWindow_Activated;
            this.PreviewKeyDown += MainPage_PreviewKeyDown;
            this.PreviewKeyUp += MainPage_PreviewKeyUp;
            this.Unloaded += MainPage_Unloaded;

            loadYoutube();
            webView.Focus(FocusState.Programmatic);
        }

        private void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Window.Current.Activated -= CurrentWindow_Activated;
            currentView.BackRequested -= CurrentView_BackRequested;
            this.PreviewKeyDown -= MainPage_PreviewKeyDown;
            this.PreviewKeyUp -= MainPage_PreviewKeyUp;
            this.Unloaded -= MainPage_Unloaded;

            _toastTimer.Stop();
            _toastTimer.Tick -= ToastTimer_Tick;

            _gamepadService.ActionTriggered -= GamepadService_ActionTriggered;
            _gamepadService.NativeGamepadCountChanged -= GamepadService_NativeGamepadCountChanged;
            _gamepadService.ControllerConnectionToastRequested -= GamepadService_ControllerConnectionToastRequested;
            _gamepadService.Dispose();
        }

        private void CurrentWindow_Activated(object sender, WindowActivatedEventArgs e)
        {
            bool isActive = e.WindowActivationState != CoreWindowActivationState.Deactivated;
            _gamepadService.SetWindowActive(isActive);
            if (isActive)
            {
                webView.Focus(FocusState.Programmatic);
            }
        }

        private void MainPage_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // Prevent UWP's built-in XAML XYFocus / gamepad navigation from stealing focus
            // away from WebView2 or double-triggering buttons handled by GamepadService.
            if (IsGamepadVirtualKey(e.Key))
            {
                e.Handled = true;
                return;
            }

            if (e.Key == VirtualKey.Escape && ControlsHelpOverlay.Visibility == Visibility.Visible)
            {
                HideControlsHelpOverlay();
                e.Handled = true;
            }
        }

        private void MainPage_PreviewKeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (IsGamepadVirtualKey(e.Key))
            {
                e.Handled = true;
            }
        }

        private static bool IsGamepadVirtualKey(VirtualKey key)
        {
            int code = (int)key;
            return code >= (int)VirtualKey.GamepadA && code <= (int)VirtualKey.GamepadRightThumbstickLeft;
        }

        private void CurrentView_BackRequested(object sender, BackRequestedEventArgs e)
        {
            e.Handled = true;

            // Avoid double-triggering when UWP raises BackRequested for the same Gamepad B press
            // that GamepadService already handled.
            if (_eventClock.ElapsedMilliseconds - _lastGamepadBackMs < BackDeduplicationWindowMs)
            {
                return;
            }

            if (ControlsHelpOverlay.Visibility == Visibility.Visible)
            {
                HideControlsHelpOverlay();
                return;
            }

            if (IsYoutubeTvLoaded())
            {
                var ignore = SendEscapeTapAsync();
            }
            else if (webView.CanGoBack)
            {
                webView.GoBack();
            }
            else
            {
                webView.Source = new Uri("https://www.youtube.com/tv#/");
            }

            webView.Focus(FocusState.Programmatic);
        }

        async void loadYoutube()
        {
            await webView.EnsureCoreWebView2Async();
            webView.CoreWebView2.Settings.UserAgent = "Mozilla/5.0 (WebOS; SmartTV) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.5283.0 Safari/537.36";
            webView.CoreWebView2.Settings.IsWebMessageEnabled = true;
            webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(GamepadService.WebGamepadBridgeScript);

            webView.Source = new Uri("https://www.youtube.com/tv#/");
            webView.CoreWebView2.NavigationCompleted += (s, e) =>
            {
                focusYoutube();
                SyncNativeGamepadCountToWeb(_gamepadService.NativeGamepadCount);
                if (!_hasShownInitialControllerToast && _gamepadService.NativeGamepadCount > 0)
                {
                    _hasShownInitialControllerToast = true;
                    ShowToast("Controller ready  •  Press L3 for controls", null, 3500);
                }
            };
            webView.CoreWebView2.WindowCloseRequested += (s, e) => { Application.Current.Exit(); };
            webView.CoreWebView2.ContextMenuRequested += (s, e) => { };
            webView.Focus(FocusState.Programmatic);
        }

        void focusYoutube()
        {
            webView.Focus(FocusState.Programmatic);
            currentView.AppViewBackButtonVisibility = AppViewBackButtonVisibility.Visible;
        }

        private void CoreWebView2_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                string message = args.TryGetWebMessageAsString();
                _gamepadService.HandleWebMessage(message);
            }
            catch (Exception)
            {
                // Ignore non-string web messages from YouTube TV scripts.
            }
        }

        private void GamepadService_NativeGamepadCountChanged(object sender, int count)
        {
            SyncNativeGamepadCountToWeb(count);
        }

        private void GamepadService_ControllerConnectionToastRequested(object sender, bool connected)
        {
            if (connected)
            {
                _hasShownInitialControllerToast = true;
                ShowToast("Controller connected  •  Press L3 for controls", null, 3500);
            }
            else
            {
                ShowToast("Controller disconnected", null, 2000);
            }
        }

        private void GamepadService_ActionTriggered(object sender, ControllerActionEvent e)
        {
            bool helpVisible = ControlsHelpOverlay.Visibility == Visibility.Visible;

            if (helpVisible)
            {
                if (e.IsPressed && !e.IsRepeat)
                {
                    if (e.Action == ControllerAction.ToggleControlsHelp ||
                        e.Action == ControllerAction.Back ||
                        e.Action == ControllerAction.Select)
                    {
                        if (e.Action == ControllerAction.Back)
                        {
                            _lastGamepadBackMs = _eventClock.ElapsedMilliseconds;
                        }
                        HideControlsHelpOverlay();
                    }
                    return;
                }

                // Always allow key-up events through while help is open so no virtual key stays stuck.
                if (e.IsPressed)
                {
                    return;
                }
            }

            if (e.Action == ControllerAction.Back && e.IsPressed)
            {
                _lastGamepadBackMs = _eventClock.ElapsedMilliseconds;
            }

            switch (e.Action)
            {
                case ControllerAction.ToggleControlsHelp:
                    if (e.IsPressed && !e.IsRepeat)
                    {
                        ShowControlsHelpOverlay();
                    }
                    return;

                case ControllerAction.ToggleFullscreen:
                    if (e.IsPressed && !e.IsRepeat)
                    {
                        ToggleFullscreen();
                    }
                    return;

                case ControllerAction.ToggleMute:
                    if (e.IsPressed && !e.IsRepeat)
                    {
                        ToggleMute();
                    }
                    return;

                case ControllerAction.VolumeUp:
                    if (e.IsPressed)
                    {
                        var ignore = AdjustVolumeAsync(0.05);
                    }
                    return;

                case ControllerAction.VolumeDown:
                    if (e.IsPressed)
                    {
                        var ignore = AdjustVolumeAsync(-0.05);
                    }
                    return;
            }

            ControllerKeySpec spec;
            if (ControllerInputProcessor.TryGetKeySpec(e.Action, out spec))
            {
                var ignore = DispatchKeyActionAsync(spec, e.IsPressed, e.IsRepeat);
            }
        }

        private async Task DispatchKeyActionAsync(ControllerKeySpec spec, bool isPressed, bool isRepeat)
        {
            CoreWebView2 core = webView.CoreWebView2;
            if (core == null)
            {
                return;
            }

            try
            {
                if (isPressed)
                {
                    if (!isRepeat)
                    {
                        int count;
                        _heldVirtualKeyCounts.TryGetValue(spec.VirtualKeyCode, out count);
                        _heldVirtualKeyCounts[spec.VirtualKeyCode] = count + 1;
                    }

                    await core.CallDevToolsProtocolMethodAsync(
                        "Input.dispatchKeyEvent",
                        spec.ToCdpKeyEventJson(true, isRepeat));
                }
                else
                {
                    int count;
                    if (!_heldVirtualKeyCounts.TryGetValue(spec.VirtualKeyCode, out count))
                    {
                        return;
                    }

                    count--;
                    if (count > 0)
                    {
                        _heldVirtualKeyCounts[spec.VirtualKeyCode] = count;
                        return;
                    }

                    _heldVirtualKeyCounts.Remove(spec.VirtualKeyCode);

                    await core.CallDevToolsProtocolMethodAsync(
                        "Input.dispatchKeyEvent",
                        spec.ToCdpKeyEventJson(false, false));
                }
            }
            catch (Exception)
            {
                // WebView2 may be mid-navigation or shutting down; ignore transient CDP errors.
            }
        }

        private async Task SendEscapeTapAsync()
        {
            ControllerKeySpec escapeSpec;
            if (!ControllerInputProcessor.TryGetKeySpec(ControllerAction.Back, out escapeSpec))
            {
                return;
            }

            CoreWebView2 core = webView.CoreWebView2;
            if (core == null)
            {
                return;
            }

            try
            {
                await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", escapeSpec.ToCdpKeyEventJson(true, false));
                await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", escapeSpec.ToCdpKeyEventJson(false, false));
            }
            catch (Exception)
            {
                // Ignore transient WebView2 errors.
            }
        }

        private bool IsYoutubeTvLoaded()
        {
            CoreWebView2 core = webView.CoreWebView2;
            if (core == null || string.IsNullOrEmpty(core.Source))
            {
                return false;
            }

            return core.Source.IndexOf("youtube.com/tv", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ToggleFullscreen()
        {
            ApplicationView view = ApplicationView.GetForCurrentView();
            if (view.IsFullScreenMode)
            {
                view.ExitFullScreenMode();
                ShowToast("Windowed Mode", null, 1800);
            }
            else if (view.TryEnterFullScreenMode())
            {
                ShowToast("Fullscreen Mode", null, 1800);
            }
            webView.Focus(FocusState.Programmatic);
        }

        private void ToggleMute()
        {
            CoreWebView2 core = webView.CoreWebView2;
            if (core == null)
            {
                return;
            }

            core.IsMuted = !core.IsMuted;
            ShowToast(core.IsMuted ? "Muted" : "Unmuted", null, 1800);
        }

        private async Task AdjustVolumeAsync(double delta)
        {
            CoreWebView2 core = webView.CoreWebView2;
            if (core == null)
            {
                return;
            }

            try
            {
                if (core.IsMuted && delta > 0)
                {
                    core.IsMuted = false;
                }

                string deltaArg = delta.ToString("0.00", CultureInfo.InvariantCulture);
                string result = await core.ExecuteScriptAsync(
                    "window.__ytTvAdjustVolume ? window.__ytTvAdjustVolume(" + deltaArg + ") : -1");

                int percent;
                if (int.TryParse(result, NumberStyles.Integer, CultureInfo.InvariantCulture, out percent) && percent >= 0)
                {
                    ShowToast("Volume  " + percent + "%", percent, 1600);
                }
            }
            catch (Exception)
            {
                // Ignore script errors while page is still loading.
            }
        }

        private async void SyncNativeGamepadCountToWeb(int count)
        {
            CoreWebView2 core = webView.CoreWebView2;
            if (core == null)
            {
                return;
            }

            try
            {
                await core.ExecuteScriptAsync(
                    "window.__ytTvSetNativeGamepadCount && window.__ytTvSetNativeGamepadCount(" + count.ToString(CultureInfo.InvariantCulture) + ");");
            }
            catch (Exception)
            {
                // Ignore if page is not ready yet; NavigationCompleted will resync.
            }
        }

        private void ShowControlsHelpOverlay()
        {
            ControlsHelpOverlay.Visibility = Visibility.Visible;
        }

        private void HideControlsHelpOverlay()
        {
            ControlsHelpOverlay.Visibility = Visibility.Collapsed;
            webView.Focus(FocusState.Programmatic);
        }

        private void ControlsHelpOverlay_Tapped(object sender, TappedRoutedEventArgs e)
        {
            HideControlsHelpOverlay();
        }

        private void ShowToast(string message, int? progressPercent, int durationMs)
        {
            StatusToastText.Text = message;
            if (progressPercent.HasValue)
            {
                StatusToastBar.Value = progressPercent.Value;
                StatusToastBar.Visibility = Visibility.Visible;
            }
            else
            {
                StatusToastBar.Visibility = Visibility.Collapsed;
            }

            StatusToast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromMilliseconds(durationMs);
            _toastTimer.Start();
        }

        private void ToastTimer_Tick(object sender, object e)
        {
            _toastTimer.Stop();
            StatusToast.Visibility = Visibility.Collapsed;
        }
    }

}
