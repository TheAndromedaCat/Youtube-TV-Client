using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Youtube_TV.Desktop.Input;
using Youtube_TV.Input;

namespace Youtube_TV.Desktop
{
    public partial class MainWindow : Window
    {
        private const string YoutubeTvUrl = "https://www.youtube.com/tv#/";
        private const string SmartTvUserAgent = "Mozilla/5.0 (WebOS; SmartTV) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.5283.0 Safari/537.36";

        private readonly DesktopGamepadService _gamepadService;
        private readonly Dictionary<int, int> _heldVirtualKeyCounts = new Dictionary<int, int>();

        private bool _isFullscreen = true;
        private WindowStyle _restoreStyle = WindowStyle.SingleBorderWindow;
        private WindowState _restoreState = WindowState.Normal;
        private ResizeMode _restoreResizeMode = ResizeMode.CanResize;
        private Rect _restoreBounds = new Rect(100, 100, 1280, 720);

        private bool _isHelpOverlayVisible;
        private bool _hasShownInitialControllerToast;

        public MainWindow()
        {
            InitializeComponent();

            _gamepadService = new DesktopGamepadService(Dispatcher);
            _gamepadService.ActionTriggered += GamepadService_ActionTriggered;
            _gamepadService.ForegroundChanged += GamepadService_ForegroundChanged;
            _gamepadService.WebFallbackSuppressedChanged += GamepadService_WebFallbackSuppressedChanged;
            _gamepadService.ControllerConnectionToastRequested += GamepadService_ControllerConnectionToastRequested;
            _gamepadService.FullscreenToggleRequested += GamepadService_FullscreenToggleRequested;
            _gamepadService.HelpOverlayDismissedFromWeb += GamepadService_HelpOverlayDismissedFromWeb;

            SourceInitialized += MainWindow_SourceInitialized;
            Loaded += MainWindow_Loaded;
            Activated += MainWindow_Activated;
            Deactivated += MainWindow_Deactivated;
            PreviewKeyDown += MainWindow_PreviewKeyDown;
            Closed += MainWindow_Closed;
        }

        private void MainWindow_SourceInitialized(object sender, EventArgs e)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            _gamepadService.AttachWindow(hwnd);
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Activate();
            _gamepadService.NotifyWindowFocusHint();
            await InitializeWebViewAsync();
        }

        private void MainWindow_Activated(object sender, EventArgs e)
        {
            _gamepadService.NotifyWindowFocusHint();
            FocusWebView();
        }

        private void MainWindow_Deactivated(object sender, EventArgs e)
        {
            _gamepadService.NotifyWindowFocusHint();
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            _gamepadService.ActionTriggered -= GamepadService_ActionTriggered;
            _gamepadService.ForegroundChanged -= GamepadService_ForegroundChanged;
            _gamepadService.WebFallbackSuppressedChanged -= GamepadService_WebFallbackSuppressedChanged;
            _gamepadService.ControllerConnectionToastRequested -= GamepadService_ControllerConnectionToastRequested;
            _gamepadService.FullscreenToggleRequested -= GamepadService_FullscreenToggleRequested;
            _gamepadService.HelpOverlayDismissedFromWeb -= GamepadService_HelpOverlayDismissedFromWeb;
            _gamepadService.Dispose();
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            Key actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
            if (actualKey == Key.F11 || (actualKey == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Alt) != 0))
            {
                SetFullscreen(!_isFullscreen, true);
                e.Handled = true;
                return;
            }

            if (actualKey == Key.Escape && _isHelpOverlayVisible)
            {
                SetHelpOverlayVisible(false);
                e.Handled = true;
            }
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TubeTV",
                    "WebView2");

                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(env);
                _gamepadService.SetWebViewBrowserProcessId(webView.CoreWebView2.BrowserProcessId);

                webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 15, 15, 15);
                webView.CoreWebView2.Settings.UserAgent = SmartTvUserAgent;
                webView.CoreWebView2.Settings.IsWebMessageEnabled = true;
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                webView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
                webView.CoreWebView2.WindowCloseRequested += (s, e) => Close();

                await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(DesktopGamepadService.DesktopWebBridgeScript);

                webView.Source = new Uri(YoutubeTvUrl);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                MessageBoxResult result = MessageBox.Show(
                    "Microsoft Edge WebView2 Runtime is required to run TubeTV.\n\nWould you like to open the download page now?",
                    "WebView2 Runtime Required",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo("https://developer.microsoft.com/en-us/microsoft-edge/webview2/")
                        {
                            UseShellExecute = true
                        });
                    }
                    catch (Exception)
                    {
                    }
                }

                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Failed to initialize YouTube TV viewer:\n\n" + ex.Message,
                    "TubeTV Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Close();
            }
        }

        private void CoreWebView2_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            webView.Visibility = Visibility.Visible;
            LoadingText.Visibility = Visibility.Collapsed;
            FocusWebView();

            SyncForegroundToWeb(_gamepadService.IsForeground);
            SyncWebFallbackSuppressedToWeb(_gamepadService.IsWebFallbackSuppressed);
            if (!_hasShownInitialControllerToast && _gamepadService.NativeGamepadCount > 0)
            {
                _hasShownInitialControllerToast = true;
                ShowToast("Controller ready  •  Press L3 for controls", null, 3500);
            }
        }

        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string message = e.TryGetWebMessageAsString();
                _gamepadService.HandleWebMessage(message);
            }
            catch (Exception)
            {
                // Ignore non-string web messages from YouTube TV scripts.
            }
        }

        private void GamepadService_ForegroundChanged(object sender, bool isForeground)
        {
            SyncForegroundToWeb(isForeground);
            if (isForeground)
            {
                FocusWebView();
            }
        }

        private void GamepadService_WebFallbackSuppressedChanged(object sender, bool suppressed)
        {
            SyncWebFallbackSuppressedToWeb(suppressed);
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

        private void GamepadService_FullscreenToggleRequested(object sender, EventArgs e)
        {
            SetFullscreen(!_isFullscreen, true);
        }

        private void GamepadService_HelpOverlayDismissedFromWeb(object sender, EventArgs e)
        {
            _isHelpOverlayVisible = false;
            FocusWebView();
        }

        private void GamepadService_ActionTriggered(object sender, ControllerActionEvent e)
        {
            if (_isHelpOverlayVisible)
            {
                if (e.IsPressed && !e.IsRepeat)
                {
                    if (e.Action == ControllerAction.ToggleControlsHelp ||
                        e.Action == ControllerAction.Back ||
                        e.Action == ControllerAction.Select)
                    {
                        SetHelpOverlayVisible(false);
                    }
                    return;
                }

                // Always allow key-up events through while help is open so no virtual key stays stuck.
                if (e.IsPressed)
                {
                    return;
                }
            }

            switch (e.Action)
            {
                case ControllerAction.ToggleControlsHelp:
                    if (e.IsPressed && !e.IsRepeat)
                    {
                        SetHelpOverlayVisible(true);
                    }
                    return;

                case ControllerAction.ToggleFullscreen:
                    if (e.IsPressed && !e.IsRepeat)
                    {
                        SetFullscreen(!_isFullscreen, true);
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
                    if (!webView.IsKeyboardFocusWithin)
                    {
                        FocusWebView();
                    }

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
                // Ignore transient CDP errors during page navigation or shutdown.
            }
        }

        private void SetFullscreen(bool enable, bool showToast)
        {
            if (_isFullscreen == enable)
            {
                return;
            }

            if (enable)
            {
                _restoreStyle = WindowStyle;
                _restoreState = WindowState;
                _restoreResizeMode = ResizeMode;
                if (WindowState == WindowState.Normal)
                {
                    _restoreBounds = new Rect(Left, Top, Width, Height);
                }

                // Resetting to Normal before changing WindowStyle ensures WPF covers the taskbar cleanly.
                if (WindowState == WindowState.Maximized)
                {
                    WindowState = WindowState.Normal;
                }

                ResizeMode = ResizeMode.NoResize;
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Maximized;
                _isFullscreen = true;
            }
            else
            {
                WindowStyle = _restoreStyle;
                ResizeMode = _restoreResizeMode;
                WindowState = WindowState.Normal;
                if (_restoreBounds.Width >= MinWidth && _restoreBounds.Height >= MinHeight)
                {
                    Left = _restoreBounds.Left;
                    Top = _restoreBounds.Top;
                    Width = _restoreBounds.Width;
                    Height = _restoreBounds.Height;
                }
                if (_restoreState == WindowState.Maximized)
                {
                    WindowState = WindowState.Maximized;
                }
                _isFullscreen = false;
            }

            if (showToast)
            {
                ShowToast(_isFullscreen ? "Fullscreen Mode" : "Windowed Mode", null, 1800);
            }

            FocusWebView();
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

        private async void SetHelpOverlayVisible(bool visible)
        {
            _isHelpOverlayVisible = visible;
            CoreWebView2 core = webView.CoreWebView2;
            if (core == null)
            {
                return;
            }

            try
            {
                await core.ExecuteScriptAsync(
                    "window.__ytTvSetHelpVisible && window.__ytTvSetHelpVisible(" + (visible ? "true" : "false") + ");");
            }
            catch (Exception)
            {
            }

            if (!visible)
            {
                FocusWebView();
            }
        }

        private async void ShowToast(string message, int? progressPercent, int durationMs)
        {
            CoreWebView2 core = webView.CoreWebView2;
            if (core == null)
            {
                return;
            }

            try
            {
                string messageJson = JsonSerializer.Serialize(message ?? string.Empty);
                string progressArg = progressPercent.HasValue
                    ? progressPercent.Value.ToString(CultureInfo.InvariantCulture)
                    : "-1";
                string durationArg = durationMs.ToString(CultureInfo.InvariantCulture);
                await core.ExecuteScriptAsync(
                    "window.__ytTvShowToast && window.__ytTvShowToast(" + messageJson + "," + progressArg + "," + durationArg + ");");
            }
            catch (Exception)
            {
            }
        }

        private async void SyncForegroundToWeb(bool isForeground)
        {
            CoreWebView2 core = webView.CoreWebView2;
            if (core == null)
            {
                return;
            }

            try
            {
                await core.ExecuteScriptAsync(
                    "window.__ytTvSetForeground && window.__ytTvSetForeground(" + (isForeground ? "true" : "false") + ");");
            }
            catch (Exception)
            {
            }
        }

        private async void SyncWebFallbackSuppressedToWeb(bool suppressed)
        {
            CoreWebView2 core = webView.CoreWebView2;
            if (core == null)
            {
                return;
            }

            try
            {
                await core.ExecuteScriptAsync(
                    "window.__ytTvSetWebFallbackSuppressed && window.__ytTvSetWebFallbackSuppressed(" + (suppressed ? "true" : "false") + ");");
            }
            catch (Exception)
            {
            }
        }

        private void FocusWebView()
        {
            if (webView.Visibility == Visibility.Visible)
            {
                webView.Focus();
                Keyboard.Focus(webView);
            }
        }
    }
}
