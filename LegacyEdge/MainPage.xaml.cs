using LegacyEdge.Models;
using LegacyEdge.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Data.Pdf;
using Windows.Data.Json;
using Windows.Devices.Input;
using Windows.Foundation;
using Windows.Graphics.Display;
using Windows.Graphics.Imaging;
using Windows.Graphics.Printing;
using Windows.Media.SpeechSynthesis;
using Windows.Networking.BackgroundTransfer;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Input;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Printing;
using Windows.Web;
using Windows.Web.Http;

namespace LegacyEdge
{
    public sealed partial class MainPage : Page
    {
        private sealed class WindowLaunchOptions
        {
            public string Address { get; set; }
            public bool IsPrivate { get; set; }
            public StorageFile File { get; set; }
        }

        private sealed class ReadingBlock
        {
            public string Kind { get; set; }
            public string Text { get; set; }
            public string Url { get; set; }
            public string Alt { get; set; }
            public int SpeechIndex { get; set; }
        }

        private sealed class ReadingContent
        {
            public string Title { get; set; }
            public string Byline { get; set; }
            public List<ReadingBlock> Blocks { get; } = new List<ReadingBlock>();
        }

        private sealed class SpeechSegment
        {
            public string Text { get; set; }
            public int ElementIndex { get; set; }
        }

        private sealed class TabPreviewCaptureTarget
        {
            public BrowserTab Tab { get; set; }
            public Image Image { get; set; }
            public FrameworkElement Fallback { get; set; }
        }

        private sealed class PdfPageVisual
        {
            public uint PageIndex { get; set; }
            public Border Host { get; set; }
            public Grid Canvas { get; set; }
            public Image Image { get; set; }
            public TextBlock Placeholder { get; set; }
        }

        private sealed class HtmlStreamUriResolver : IUriToStreamResolver
        {
            private readonly string _html;

            public HtmlStreamUriResolver(string html)
            {
                _html = html ?? string.Empty;
            }

            public IAsyncOperation<IInputStream> UriToStreamAsync(Uri uri)
            {
                return CreateStreamAsync().AsAsyncOperation();
            }

            private async Task<IInputStream> CreateStreamAsync()
            {
                var stream = new InMemoryRandomAccessStream();
                var writer = new DataWriter(stream)
                {
                    UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8,
                    ByteOrder = ByteOrder.LittleEndian
                };
                writer.WriteString(_html);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
                writer.Dispose();
                stream.Seek(0);
                return stream.GetInputStreamAt(0);
            }
        }

        private static readonly object SessionSync = new object();
        private static readonly Dictionary<int, List<string>> SessionAddressesByView = new Dictionary<int, List<string>>();
        private readonly List<BrowserTab> _tabs = new List<BrowserTab>();
        private readonly Stack<string> _closedTabs = new Stack<string>();
        private ObservableCollection<BrowserItem> _favorites;
        private ObservableCollection<BrowserItem> _readingList;
        private ObservableCollection<BrowserItem> _history;
        private ObservableCollection<DownloadItem> _downloads;
        private readonly ObservableCollection<DownloadItem> _activeDownloads = new ObservableCollection<DownloadItem>();
        private readonly ObservableCollection<DownloadItem> _pastDownloads = new ObservableCollection<DownloadItem>();
        private readonly object _downloadSnapshotDispatchSync = new object();
        private bool _downloadSnapshotDispatchPending;
        private bool _pendingDownloadShelfNotification;
        private readonly ObservableCollection<BrowserItem> _books = new ObservableCollection<BrowserItem>();
        private readonly ObservableCollection<BrowserItem> _setAsideTabs = new ObservableCollection<BrowserItem>();
        private readonly ObservableCollection<BrowserItem> _openTabsSnapshot = new ObservableCollection<BrowserItem>();
        private readonly ObservableCollection<OmniboxSuggestion> _omniboxSuggestions = new ObservableCollection<OmniboxSuggestion>();
        private readonly List<WebView> _retiredWebViews = new List<WebView>();
        private readonly HashSet<BrowserTab> _findHighlightedTabs = new HashSet<BrowserTab>();
        private readonly Dictionary<BrowserTab, Border> _tabHeaders = new Dictionary<BrowserTab, Border>();
        private readonly Dictionary<BrowserTab, Border> _tabDropCues = new Dictionary<BrowserTab, Border>();
        private readonly Dictionary<WebView, string> _sourceDocumentAddresses = new Dictionary<WebView, string>();
        private WebView _capturingWebView;
        private bool _pageUnloadPending;
        private int _pageLifecycleGeneration;
        private BrowserTab _deferredTabSwitch;
        private BrowserTab _deferredTabClose;
        private BrowserTab _currentTab;
        private readonly DispatcherTimer _tabDragAutoScrollTimer;
        private BrowserTab _tabDragTab;
        private BrowserTab _suppressTabTapFor;
        private Border _tabDragHeader;
        private Border _tabDragCue;
        private Pointer _tabDragPointer;
        private TranslateTransform _tabDragTransform;
        private double _tabDragPressContentX;
        private double _tabDragPressY;
        private double _tabDragLastScrollerX;
        private double _tabDragLastY;
        private int _tabDragTargetIndex = -1;
        private int _tabDragScrollDirection;
        private int _tabStructureVersion;
        private int _tabDragStructureVersion;
        private int _tabTapSuppressionGeneration;
        private int _tabDeferredRebuildGeneration;
        private bool _tabDragActive;
        private bool _tabRebuildDeferred;
        private string _activationAddress;
        private StorageFile _activationFile;
        private bool _activationPrivate;
        private bool _hasWindowLaunchOptions;
        private bool _windowIsPrivate;
        private string _activeHub = "favorites";
        private bool _hubExpanded = true;
        private bool _hubPinned;
        private string _downloadShelfItemId;
        private LegacyDownloadState? _downloadShelfLastState;
        private bool _downloadShelfHidden;
        private Uri _pendingDownloadUri;
        private bool _pendingDownloadActionBusy;
        private string _openAfterDownloadId;
        private bool _initialized;
        private bool _dialogOpen;
        private bool _moreMenuOpen;
        private Flyout _activeMoreSubmenu;
        private bool _loadingSettings;
        private string _activeSettingsSection = "general";
        private bool _isFullScreen;
        private DispatcherTimer _statusTimer;
        private BrowserTab _contextTab;
        private string _contextLinkUrl;
        private string _contextImageUrl;
        private string _contextSelectedText;
        private bool _contextIsEditable;
        private bool _contextPollBusy;
        private bool _recoveringWebViewProcess;
        private readonly DispatcherTimer _contextPollTimer;
        private readonly int _viewId;
        private SpeechSynthesizer _readAloudSynthesizer;
        private SpeechSynthesisStream _readAloudStream;
        private readonly List<SpeechSegment> _readAloudSegments = new List<SpeechSegment>();
        private int _readAloudIndex = -1;
        private int _readAloudGeneration;
        private bool _readAloudPlaying;
        private bool _readAloudCompleted;
        private bool _readAloudVoicesReady;
        private BrowserTab _readAloudTab;
        private int _tabPreviewGeneration;
        private readonly Dictionary<uint, PdfPageVisual> _pdfPageVisuals = new Dictionary<uint, PdfPageVisual>();
        private bool _pdfRenderBusy;
        private int _pdfRenderGeneration;
        private bool _pdfSizeUpdating;
        private Guid _pdfVisualTabId = Guid.Empty;
        private bool _omniboxEditing;
        private bool _suppressOmniboxTextChanged;
        private string _omniboxOriginalText = string.Empty;
        private string _omniboxTypedText = string.Empty;
        private string _findQuery = string.Empty;
        private int _findMatchCount;
        private int _findMatchIndex;
        private int _findGeneration;
        private bool _findMatchCountTruncated;
        private Task _findInitializationTask = Task.CompletedTask;
        private PrintManager _printManager;
        private PrintDocument _printDocument;
        private IPrintDocumentSource _printDocumentSource;
        private BitmapImage _printPreviewBitmap;
        private Grid _printPage;
        private string _printTitle = "Legacy Edge";
        private string _printAddress = string.Empty;
        private bool _printRegistered;
        private bool _printPreparing;
        private int _printOperationGeneration;
        private double _captionButtonsWidth = 138;
        private bool IsDarkTheme => string.Equals(BrowserDataStore.AppTheme, "Dark", StringComparison.Ordinal);

        public MainPage()
        {
            InitializeComponent();
            OmniboxList.ItemsSource = _omniboxSuggestions;
            NavigationCacheMode = NavigationCacheMode.Required;
            _viewId = ApplicationView.GetForCurrentView().Id;

            var coreTitleBar = CoreApplication.GetCurrentView().TitleBar;
            coreTitleBar.ExtendViewIntoTitleBar = true;
            coreTitleBar.LayoutMetricsChanged += CoreTitleBar_LayoutMetricsChanged;
            Window.Current.SetTitleBar(TitleBarDragRegion);
            UpdateCaptionButtonsWidth(coreTitleBar);
            ApplyTheme(false);
            ApplicationView.GetForCurrentView().SetPreferredMinSize(new Size(500, 320));

            NoteCanvas.InkPresenter.InputDeviceTypes = CoreInputDeviceTypes.Pen | CoreInputDeviceTypes.Mouse | CoreInputDeviceTypes.Touch;
            _contextPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _contextPollTimer.Tick += ContextPollTimer_Tick;
            _tabDragAutoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _tabDragAutoScrollTimer.Tick += TabDragAutoScrollTimer_Tick;

            // A XAML KeyboardAccelerator has application-wide scope, including
            // when the EdgeHTML child has keyboard focus. Keep the CoreWindow
            // accelerator handler below as a fallback for older input paths.
            var findAccelerator = new KeyboardAccelerator
            {
                Key = VirtualKey.F,
                Modifiers = VirtualKeyModifiers.Control
            };
            findAccelerator.Invoked += FindKeyboardAccelerator_Invoked;
            KeyboardAccelerators.Add(findAccelerator);
        }

        private void ApplyTheme(bool refreshStartPages)
        {
            var elementTheme = IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light;
            var webViewBackground = IsDarkTheme ? Color.FromArgb(255, 31, 31, 31) : Colors.White;
            RequestedTheme = elementTheme;
            OmniboxBorder.RequestedTheme = elementTheme;
            SidePaneBorder.RequestedTheme = elementTheme;
            LegacySupportBorder.RequestedTheme = elementTheme;
            UpdateAddressBoxFocusColors(FocusManager.GetFocusedElement() == AddressBox);
            ApplyTitleBarTheme(_currentTab?.IsPrivate == true);

            foreach (var tab in _tabs.Where(tab => tab.View != null))
            {
                tab.View.RequestedTheme = elementTheme;
                tab.View.DefaultBackgroundColor = webViewBackground;
            }

            if (_currentTab != null) UpdateChrome();
            RebuildTabs();
            if (TabPreviewBar.Visibility == Visibility.Visible) RebuildTabPreviewCards(false);
            RebuildFavoritesBar();

            if (!refreshStartPages) return;
            foreach (var tab in _tabs.Where(tab => tab.View != null && !tab.IsPdfView && tab.LocalFile == null && string.IsNullOrWhiteSpace(tab.Address)))
                tab.View.NavigateToString(BuildStartPageHtml(tab.IsPrivate));
        }

        private async void CoreTitleBar_LayoutMetricsChanged(CoreApplicationViewTitleBar sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => UpdateCaptionButtonsWidth(sender));
        }

        private void UpdateCaptionButtonsWidth(CoreApplicationViewTitleBar titleBar)
        {
            if (titleBar == null || CaptionButtonsColumn == null) return;
            var measured = titleBar.SystemOverlayRightInset;
            if (measured <= 0 && !ApplicationView.GetForCurrentView().IsFullScreenMode) measured = 138;
            _captionButtonsWidth = Math.Max(0, measured);
            CaptionButtonsColumn.Width = new GridLength(_captionButtonsWidth);
            if (_initialized) RebuildTabs();
        }

        private void ApplyTitleBarTheme(bool isPrivate)
        {
            var dark = IsDarkTheme;
            TitleBar.RequestedTheme = dark || isPrivate ? ElementTheme.Dark : ElementTheme.Light;
            var titleColor = isPrivate
                ? (dark ? Color.FromArgb(255, 55, 35, 70) : Color.FromArgb(255, 82, 48, 107))
                : (dark ? Color.FromArgb(255, 31, 31, 31) : Color.FromArgb(255, 204, 204, 204));
            var previewColor = isPrivate
                ? (dark ? Color.FromArgb(255, 48, 31, 61) : Color.FromArgb(255, 70, 40, 91))
                : titleColor;
            var foreground = dark || isPrivate ? Colors.White : Colors.Black;
            var inactiveForeground = dark || isPrivate ? Color.FromArgb(180, 255, 255, 255) : Color.FromArgb(150, 0, 0, 0);
            var hover = isPrivate
                ? (dark ? Color.FromArgb(255, 76, 49, 94) : Color.FromArgb(255, 103, 72, 123))
                : (dark ? Color.FromArgb(255, 65, 65, 65) : Color.FromArgb(255, 225, 225, 225));
            var pressed = dark ? Color.FromArgb(255, 80, 80, 80) : Color.FromArgb(255, 184, 184, 184);

            TitleBar.Background = new SolidColorBrush(titleColor);
            TabPreviewBar.Background = new SolidColorBrush(previewColor);
            TabPreviewEmptyText.Foreground = new SolidColorBrush(dark || isPrivate ? Color.FromArgb(220, 255, 255, 255) : Color.FromArgb(255, 90, 90, 90));
            NewTabButton.Foreground = new SolidColorBrush(foreground);

            var systemTitleBar = ApplicationView.GetForCurrentView().TitleBar;
            systemTitleBar.ButtonBackgroundColor = titleColor;
            systemTitleBar.ButtonInactiveBackgroundColor = titleColor;
            systemTitleBar.ButtonForegroundColor = foreground;
            systemTitleBar.ButtonInactiveForegroundColor = inactiveForeground;
            systemTitleBar.ButtonHoverBackgroundColor = hover;
            systemTitleBar.ButtonHoverForegroundColor = foreground;
            systemTitleBar.ButtonPressedBackgroundColor = pressed;
            systemTitleBar.ButtonPressedForegroundColor = foreground;
        }

        private void ApplyWindowPrivacyChrome()
        {
            if (TitleSetAsideColumn == null || SetAsideControls == null || TabPreviewScroller == null) return;

            var setAsideWidth = _windowIsPrivate ? 0 : 80;
            TitleSetAsideColumn.Width = new GridLength(setAsideWidth);
            SetAsideControls.Visibility = _windowIsPrivate ? Visibility.Collapsed : Visibility.Visible;
            TabPreviewScroller.Margin = new Thickness(setAsideWidth, 8, 8, 8);
            TabPreviewEmptyText.Margin = new Thickness(setAsideWidth + 18, 0, 0, 0);
            ApplyTitleBarTheme(_windowIsPrivate);
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            if (e.Parameter is WindowLaunchOptions options)
            {
                _activationAddress = options.Address;
                _activationPrivate = options.IsPrivate;
                _windowIsPrivate = options.IsPrivate;
                _activationFile = options.File;
                _hasWindowLaunchOptions = true;
            }
            else if (e.Parameter is StorageFile file)
            {
                _activationFile = file;
            }
            else
            {
                _activationAddress = e.Parameter as string;
            }
            base.OnNavigatedTo(e);
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            _pageUnloadPending = false;
            ++_pageLifecycleGeneration;
            if (_initialized) return;
            _initialized = true;
            ApplyWindowPrivacyChrome();

            _favorites = new ObservableCollection<BrowserItem>(BrowserDataStore.LoadFavorites());
            _readingList = new ObservableCollection<BrowserItem>(BrowserDataStore.LoadReadingList());
            _history = new ObservableCollection<BrowserItem>(BrowserDataStore.LoadHistory());
            _downloads = new ObservableCollection<DownloadItem>();
            ActiveDownloadsList.ItemsSource = _activeDownloads;
            PastDownloadsList.ItemsSource = _pastDownloads;
            DownloadCoordinator.Current.SnapshotsChanged += DownloadCoordinator_SnapshotsChanged;
            ApplyDownloadSnapshots(DownloadCoordinator.Current.GetSnapshots(), false);
            foreach (var item in BrowserDataStore.LoadSetAsideTabs()) _setAsideTabs.Add(item);

            BrowserDataStore.RecoverHomeButtonAfterChromeFix();
            FavoritesBar.Visibility = BrowserDataStore.ShowFavoritesBar ? Visibility.Visible : Visibility.Collapsed;
            ApplyHomeButtonVisibility();
            RebuildFavoritesBar();
            UpdateLegacySupportPopup();
            _ = InitializeDownloadCoordinatorAsync();

            Window.Current.CoreWindow.Dispatcher.AcceleratorKeyActivated += Dispatcher_AcceleratorKeyActivated;
            DataTransferManager.GetForCurrentView().DataRequested += ShareManager_DataRequested;
            RegisterForPrinting();
            _contextPollTimer.Start();

            if (_activationFile != null)
            {
                var file = _activationFile;
                _activationFile = null;
                var tab = CreateTab(null, true, _activationPrivate, false);
                _activationPrivate = false;
                _hasWindowLaunchOptions = false;
                _ = OpenLocalFileAsync(tab, file);
                return;
            }

#if DEBUG
            if (!string.IsNullOrWhiteSpace(_activationAddress) && TryHandleDebugDownloadActivation(_activationAddress))
            {
                _activationAddress = null;
                return;
            }
#endif

            if (_hasWindowLaunchOptions)
            {
                CreateTab(_activationAddress, true, _activationPrivate);
                _activationAddress = null;
                _activationPrivate = false;
                _hasWindowLaunchOptions = false;
                return;
            }

            if (!string.IsNullOrWhiteSpace(_activationAddress))
            {
                CreateTab(_activationAddress, true);
                _activationAddress = null;
                return;
            }

            var startupMode = BrowserDataStore.StartupMode;
            var session = startupMode == "PreviousPages" ? BrowserDataStore.LoadSession() : new List<string>();
            if (session.Count > 0)
            {
                foreach (var address in session)
                    CreateTab(address, false);
                SwitchTab(_tabs[0]);
            }
            else if (startupMode == "SpecificPage")
            {
                CreateTab(BrowserDataStore.StartupPage, true);
            }
            else
            {
                CreateTab(null, true);
            }
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            if (!_initialized) return;
            _pageUnloadPending = true;
            ++_pageLifecycleGeneration;
            if (_capturingWebView != null) return;
            CompletePageUnload();
        }

        private void CompletePageUnload()
        {
            if (!_initialized || _capturingWebView != null) return;
            EndTabPointerGesture(false, true, true, false);
            _tabDragAutoScrollTimer.Stop();
            StopReadAloud(true);
            SaveSession();
            UnregisterSessionSnapshot();
            Window.Current.CoreWindow.Dispatcher.AcceleratorKeyActivated -= Dispatcher_AcceleratorKeyActivated;
            DataTransferManager.GetForCurrentView().DataRequested -= ShareManager_DataRequested;
            UnregisterForPrinting();
            DownloadCoordinator.Current.SnapshotsChanged -= DownloadCoordinator_SnapshotsChanged;
            _contextPollTimer.Stop();
            _initialized = false;
            ++_tabPreviewGeneration;
            ++_findGeneration;
            _contextTab = null;
            foreach (var tab in _tabs.ToList()) RetireWebView(tab);
            _tabs.Clear();
            ++_tabStructureVersion;
            _currentTab = null;
            _pageUnloadPending = false;
        }

        private async Task InitializeDownloadCoordinatorAsync()
        {
            await DownloadCoordinator.Current.InitializeAsync();
            var snapshots = DownloadCoordinator.Current.GetSnapshots();
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => ApplyDownloadSnapshots(snapshots, false));
            if (!DownloadCoordinator.Current.IsInitialized && !string.IsNullOrWhiteSpace(DownloadCoordinator.Current.LastInitializationError))
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => ShowTransientStatus(DownloadCoordinator.Current.LastInitializationError));
        }

        private async void DownloadCoordinator_SnapshotsChanged(object sender, DownloadSnapshotsChangedEventArgs e)
        {
            if (!_initialized || e == null) return;
            lock (_downloadSnapshotDispatchSync)
            {
                _pendingDownloadShelfNotification |= !string.IsNullOrWhiteSpace(e.ChangedId);
                if (_downloadSnapshotDispatchPending) return;
                _downloadSnapshotDispatchPending = true;
            }

            try
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    IReadOnlyList<DownloadItem> snapshots;
                    bool notifyShelf;
                    lock (_downloadSnapshotDispatchSync)
                    {
                        // Fetch the broker's current state on the UI dispatcher.
                        // Concurrent publishers can otherwise arrive out of order
                        // and let an older detached snapshot replace a newer one.
                        snapshots = DownloadCoordinator.Current.GetSnapshots();
                        notifyShelf = _pendingDownloadShelfNotification;
                        _pendingDownloadShelfNotification = false;
                        _downloadSnapshotDispatchPending = false;
                    }
                    if (_initialized) ApplyDownloadSnapshots(snapshots, notifyShelf);
                });
            }
            catch
            {
                lock (_downloadSnapshotDispatchSync)
                {
                    _pendingDownloadShelfNotification = false;
                    _downloadSnapshotDispatchPending = false;
                }
            }
        }

        private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateAdaptiveChrome();
            PositionOmniboxPopup();
            PositionSidePane();
            PositionLegacySupportPopup();
            RebuildTabs();
        }

        public void OpenExternalAddress(string address)
        {
#if DEBUG
            if (_initialized && TryHandleDebugDownloadActivation(address)) return;
#endif
            if (!_initialized)
                _activationAddress = address;
            else
                CreateTab(address, true);
        }

        public void OpenExternalFile(StorageFile file)
        {
            if (file == null) return;
            if (!_initialized) _activationFile = file;
            else
            {
                var tab = CreateTab(null, true, false, false);
                _ = OpenLocalFileAsync(tab, file);
            }
        }

        public void SaveSession()
        {
            UpdateSessionSnapshot();
            SaveCombinedSession(CoreApplication.GetCurrentView().IsMain && _tabs.Any(tab => !tab.IsPrivate));
        }

        private List<string> CurrentSessionAddresses() => _tabs
            .Where(tab => !tab.IsPrivate && (!string.IsNullOrWhiteSpace(tab.Address) || !string.IsNullOrWhiteSpace(tab.LocalFileToken)))
            .Select(tab => !string.IsNullOrWhiteSpace(tab.LocalFileToken) ? "legacyedge-file:" + tab.LocalFileToken : tab.Address)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        private void UpdateSessionSnapshot()
        {
            var addresses = CurrentSessionAddresses();
            lock (SessionSync)
            {
                if (addresses.Count == 0) SessionAddressesByView.Remove(_viewId);
                else SessionAddressesByView[_viewId] = addresses;
            }
        }

        private static void SaveCombinedSession(bool allowEmpty)
        {
            List<string> addresses;
            lock (SessionSync)
            {
                addresses = SessionAddressesByView
                    .OrderBy(entry => entry.Key)
                    .SelectMany(entry => entry.Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(20)
                    .ToList();
            }
            if (addresses.Count > 0 || allowEmpty) BrowserDataStore.SaveSession(addresses);
        }

        private void UnregisterSessionSnapshot()
        {
            var hasRemainingSession = false;
            lock (SessionSync)
            {
                SessionAddressesByView.Remove(_viewId);
                hasRemainingSession = SessionAddressesByView.Values.Any(items => items.Count > 0);
            }
            if (hasRemainingSession) SaveCombinedSession(false);
        }

        private WebView CreateConfiguredWebView()
        {
            // Keep EdgeHTML off the UI thread without using the shared
            // out-of-process host that produced native XAML fail-fast crashes.
            var view = new WebView(WebViewExecutionMode.SeparateThread)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                RequestedTheme = IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light,
                DefaultBackgroundColor = IsDarkTheme ? Color.FromArgb(255, 31, 31, 31) : Colors.White,
                Visibility = Visibility.Collapsed
            };
            view.Settings.IsJavaScriptEnabled = true;
            view.Settings.IsIndexedDBEnabled = true;
            view.NavigationStarting += WebView_NavigationStarting;
            view.NavigationCompleted += WebView_NavigationCompleted;
            view.NewWindowRequested += WebView_NewWindowRequested;
            view.PermissionRequested += WebView_PermissionRequested;
            view.UnsupportedUriSchemeIdentified += WebView_UnsupportedUriSchemeIdentified;
            view.UnviewableContentIdentified += WebView_UnviewableContentIdentified;
            view.ContainsFullScreenElementChanged += WebView_ContainsFullScreenElementChanged;
            view.LongRunningScriptDetected += WebView_LongRunningScriptDetected;
            return view;
        }

        private BrowserTab CreateTab(string address = null, bool switchTo = true, bool isPrivate = false, bool showStartPage = true)
        {
            var view = CreateConfiguredWebView();

            // InPrivate is a window mode in legacy Edge. Never allow an action
            // inside a private window to create a normal tab accidentally.
            var tab = new BrowserTab { View = view, IsPrivate = _windowIsPrivate || isPrivate };
            _tabs.Add(tab);
            ++_tabStructureVersion;

            var activated = switchTo && SwitchTab(tab);
            if (!switchTo) RebuildTabs();

            if (!string.IsNullOrWhiteSpace(address))
            {
                if (activated) Navigate(tab, address);
                else
                {
                    tab.PendingAddress = address;
                    tab.Address = NormalizeAddress(address);
                    tab.Title = HostLabel(tab.Address);
                }
            }
            else if (showStartPage)
            {
                if (activated) ShowStartPage(tab);
                else tab.PendingAddress = "about:blank";
            }

            UpdateSessionSnapshot();
            return tab;
        }

        private void ShowStartPage(BrowserTab tab)
        {
            ClearPdfState(tab);
            ClearLocalFileState(tab);
            tab.Title = tab.IsPrivate ? "InPrivate" : "New tab";
            tab.Address = string.Empty;
            tab.View?.NavigateToString(BuildStartPageHtml(tab.IsPrivate));
            UpdateSessionSnapshot();
            UpdateChrome();
        }

        private bool SwitchTab(BrowserTab tab)
        {
            if (tab == null || !_tabs.Contains(tab)) return false;
            if (_capturingWebView != null && _currentTab != tab && ReferenceEquals(_currentTab?.View, _capturingWebView))
            {
                _deferredTabSwitch = tab;
                return false;
            }
            var previousTab = _currentTab;
            var changedTab = !ReferenceEquals(previousTab, tab);
            var clearFindBeforeReveal = changedTab && !tab.IsPdfView &&
                FindBar.Visibility != Visibility.Visible && _findHighlightedTabs.Contains(tab) &&
                string.IsNullOrWhiteSpace(tab.PendingAddress);
            if (changedTab)
            {
                ++_findGeneration;
                _findQuery = string.Empty;
                _findMatchCount = 0;
                _findMatchIndex = 0;
                _findMatchCountTruncated = false;
                FindCountText.Text = "0 of 0";
            }
            CloseOmniboxPopup(false, true);
            if (NoteSurface.Visibility == Visibility.Visible) ExitNoteMode();
            if (_readAloudTab != null && _readAloudTab != tab) StopReadAloud();
            if (_currentTab?.IsPdfView == true) _currentTab.PdfVerticalOffset = PdfScrollViewer.VerticalOffset;

            foreach (var attached in BrowserHost.Children.OfType<WebView>().Where(view => !ReferenceEquals(view, tab.View)).ToList())
                DetachDormantWebView(attached);

            _currentTab = tab;
            if (tab.View == null)
            {
                tab.View = CreateConfiguredWebView();
                ++tab.ViewGeneration;
            }
            if (tab.IsPdfView)
            {
                DetachDormantWebView(tab.View);
            }
            else
            {
                if (!BrowserHost.Children.Contains(tab.View)) BrowserHost.Children.Add(tab.View);
                tab.View.Visibility = clearFindBeforeReveal ? Visibility.Collapsed : Visibility.Visible;
            }

            if (tab.IsPdfView) ShowPdfTab(tab); else HidePdfSurface();

            UpdateChrome();
            RebuildTabs();

            var pendingAddress = tab.PendingAddress;
            tab.PendingAddress = null;
            if (!string.IsNullOrWhiteSpace(pendingAddress))
            {
                if (string.Equals(NormalizeAddress(pendingAddress), "about:blank", StringComparison.OrdinalIgnoreCase)) ShowStartPage(tab);
                else Navigate(tab, pendingAddress);
            }
            else if (changedTab && !tab.IsPdfView)
            {
                if (FindBar.Visibility == Visibility.Visible && !string.IsNullOrEmpty(FindBox.Text) && !tab.IsLoading)
                    _findInitializationTask = InitializeFindAsync();
                else if (FindBar.Visibility != Visibility.Visible && _findHighlightedTabs.Contains(tab))
                    _ = ClearFindHighlightsAndRevealAsync(tab, tab.View, tab.ViewGeneration, _findGeneration);
            }
            return true;
        }

        private void CloseTab(BrowserTab tab, bool rememberClosed = true)
        {
            var index = _tabs.IndexOf(tab);
            if (index < 0) return;
            if (_capturingWebView != null && ReferenceEquals(tab.View, _capturingWebView))
            {
                _deferredTabClose = tab;
                return;
            }

            if (_readAloudTab == tab) StopReadAloud();
            if (tab == _currentTab && NoteSurface.Visibility == Visibility.Visible) ExitNoteMode();
            if (tab == _currentTab && tab.IsPdfView) HidePdfSurface();

            if (rememberClosed && !tab.IsPrivate && (!string.IsNullOrWhiteSpace(tab.Address) || !string.IsNullOrWhiteSpace(tab.LocalFileToken)))
                _closedTabs.Push(!string.IsNullOrWhiteSpace(tab.LocalFileToken) ? "legacyedge-file:" + tab.LocalFileToken : tab.Address);

            RetireWebView(tab);
            _tabs.RemoveAt(index);
            ++_tabStructureVersion;
            UpdateSessionSnapshot();

            if (_tabs.Count == 0)
            {
                _currentTab = null;
                CreateTab();
                return;
            }

            if (_currentTab == tab)
                SwitchTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
            else
                RebuildTabs();
        }

        private void DetachWebView(WebView view)
        {
            if (view == null) return;
            _sourceDocumentAddresses.Remove(view);
            view.NavigationStarting -= WebView_NavigationStarting;
            view.NavigationCompleted -= WebView_NavigationCompleted;
            view.NewWindowRequested -= WebView_NewWindowRequested;
            view.PermissionRequested -= WebView_PermissionRequested;
            view.UnsupportedUriSchemeIdentified -= WebView_UnsupportedUriSchemeIdentified;
            view.UnviewableContentIdentified -= WebView_UnviewableContentIdentified;
            view.ContainsFullScreenElementChanged -= WebView_ContainsFullScreenElementChanged;
            view.LongRunningScriptDetected -= WebView_LongRunningScriptDetected;
            view.SeparateProcessLost -= WebView_SeparateProcessLost;
        }

        private void DetachDormantWebView(WebView view)
        {
            if (view == null) return;
            view.Visibility = Visibility.Collapsed;
            BrowserHost?.Children.Remove(view);
        }

        private void RetireWebView(BrowserTab tab)
        {
            var view = tab?.View;
            _findHighlightedTabs.Remove(tab);
            if (view == null) return;

            ++tab.ViewGeneration;
            if (_contextTab == tab) _contextTab = null;
            try { view.Visibility = Visibility.Collapsed; } catch { }
            try { view.Stop(); } catch { }
            DetachWebView(view);
            BrowserHost?.Children.Remove(view);
            if (ReferenceEquals(tab.View, view)) tab.View = null;
            QuarantineRetiredWebView(view);
        }

        private async void QuarantineRetiredWebView(WebView view)
        {
            // XAML unregisters component hosts on a later render pass. Keep the
            // dependency object alive briefly so a queued tick cannot observe a
            // destroyed WebView while that unregister propagates.
            _retiredWebViews.Add(view);
            await Task.Delay(750);
            try
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => _retiredWebViews.Remove(view));
            }
            catch { }
        }

        private bool IsLiveWebView(BrowserTab tab, WebView view, int generation)
        {
            return _initialized && !_pageUnloadPending && tab != null && view != null && _tabs.Contains(tab) &&
                tab.ViewGeneration == generation && ReferenceEquals(tab.View, view);
        }

        private bool IsPageOperationLive(int lifecycleGeneration)
        {
            return _initialized && !_pageUnloadPending && lifecycleGeneration == _pageLifecycleGeneration;
        }

        private bool TryBeginWebViewCapture(WebView view)
        {
            if (!_initialized || _pageUnloadPending || view == null || _capturingWebView != null) return false;
            _capturingWebView = view;
            return true;
        }

        private void EndWebViewCapture(WebView view)
        {
            if (!ReferenceEquals(_capturingWebView, view)) return;
            _capturingWebView = null;
            var lifecycleGeneration = _pageLifecycleGeneration;
            var deferredClose = _deferredTabClose;
            var deferredSwitch = _deferredTabSwitch;
            _deferredTabClose = null;
            _deferredTabSwitch = null;
            try
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    if (!_initialized || lifecycleGeneration != _pageLifecycleGeneration) return;
                    if (_pageUnloadPending)
                    {
                        if (_capturingWebView == null) CompletePageUnload();
                        return;
                    }
                    if (deferredClose != null && _tabs.Contains(deferredClose)) CloseTab(deferredClose);
                    else if (deferredSwitch != null && _tabs.Contains(deferredSwitch)) SwitchTab(deferredSwitch);
                });
            }
            catch
            {
                // The dispatcher can already be gone during final window shutdown.
            }
        }

        private void WebView_SeparateProcessLost(WebView sender, WebViewSeparateProcessLostEventArgs args)
        {
            if (_recoveringWebViewProcess || FindTab(sender) == null) return;
            _recoveringWebViewProcess = true;
            try
            {
                if (_readAloudTab != null) StopReadAloud();
                if (_isFullScreen) ExitFullScreen();

                var tabs = _tabs.ToList();
                foreach (var tab in tabs) ReplaceLostWebView(tab);
                foreach (var tab in tabs) RestoreTabAfterWebViewLoss(tab);

                if (_currentTab?.IsPdfView == true) ShowPdfTab(_currentTab);
                UpdateChrome();
                RebuildTabs();
                ShowTransientStatus("Web content stopped unexpectedly. Your tabs were reloaded.");
            }
            finally
            {
                _recoveringWebViewProcess = false;
            }
        }

        private void ReplaceLostWebView(BrowserTab tab)
        {
            if (tab?.View == null) return;
            RetireWebView(tab);

            var replacement = CreateConfiguredWebView();
            replacement.Visibility = tab == _currentTab && !tab.IsPdfView ? Visibility.Visible : Visibility.Collapsed;
            tab.View = replacement;
            tab.IsLoading = false;
            tab.PreviewImage = null;
            if (tab.IsPdfView) tab.PdfReturnNeedsReload = true;

            if (tab == _currentTab && !tab.IsPdfView) BrowserHost.Children.Add(replacement);
        }

        private void RestoreTabAfterWebViewLoss(BrowserTab tab)
        {
            if (tab == null || tab.IsPdfView) return;
            if (tab.LocalFile != null)
            {
                var file = tab.LocalFile;
                var token = tab.LocalFileToken;
                _ = OpenLocalFileAsync(tab, file, token);
                return;
            }

            var address = tab.IsReadingView ? tab.OriginalAddress : tab.Address;
            tab.IsReadingView = false;
            tab.OriginalAddress = null;
            if (string.IsNullOrWhiteSpace(address) || string.Equals(address, "about:blank", StringComparison.OrdinalIgnoreCase))
            {
                ShowStartPage(tab);
                return;
            }

            Navigate(tab, address);
        }

        private BrowserTab FindTab(WebView view) => _tabs.FirstOrDefault(tab => tab.View == view);

        private void RebuildTabs()
        {
            if (TabsPanel == null) return;
            if (_tabDragPointer != null)
            {
                if (_tabStructureVersion == _tabDragStructureVersion)
                {
                    _tabRebuildDeferred = true;
                    return;
                }

                EndTabPointerGesture(false, true, true, false);
            }

            _tabRebuildDeferred = false;
            ++_tabDeferredRebuildGeneration;
            TabsPanel.Children.Clear();
            _tabHeaders.Clear();
            _tabDropCues.Clear();
            var setAsideControlsWidth = _windowIsPrivate ? 0 : 80;
            var available = Math.Max(140, ActualWidth - setAsideControlsWidth - 40 - 40 - _captionButtonsWidth - 80);
            var pinnedCount = _tabs.Count(tab => tab.IsPinned);
            var regularCount = Math.Max(0, _tabs.Count - pinnedCount);
            var regularAvailable = Math.Max(140, available - (pinnedCount * 48));
            var tabWidth = Math.Max(140, Math.Min(252, regularAvailable / Math.Max(1, regularCount)));
            TabsScroller.Width = Math.Min(available, (pinnedCount * 48) + (tabWidth * regularCount));
            var activeBackground = IsDarkTheme ? Color.FromArgb(255, 23, 23, 23) : Color.FromArgb(255, 242, 242, 242);
            var borderColor = IsDarkTheme ? Color.FromArgb(90, 255, 255, 255) : Color.FromArgb(65, 0, 0, 0);
            var chromeText = IsDarkTheme ? Colors.White : Color.FromArgb(255, 27, 27, 27);
            var fallbackColor = IsDarkTheme ? Color.FromArgb(255, 196, 196, 196) : Color.FromArgb(255, 65, 65, 65);
            var hoverBackground = IsDarkTheme ? Color.FromArgb(255, 51, 51, 51) : Color.FromArgb(255, 218, 218, 218);

            foreach (var tab in _tabs)
            {
                var inactivePrivate = tab.IsPrivate && tab != _currentTab;
                var tabText = inactivePrivate ? Colors.White : chromeText;
                var header = new Border
                {
                    Width = tab.IsPinned ? 48 : tabWidth,
                    Height = 40,
                    Background = tab == _currentTab ? new SolidColorBrush(activeBackground) : new SolidColorBrush(Colors.Transparent),
                    BorderBrush = new SolidColorBrush(borderColor),
                    BorderThickness = new Thickness(0, 0, 1, 0),
                    Tag = tab
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

                var iconHost = new Grid
                {
                    Width = 16,
                    Height = 16,
                    HorizontalAlignment = tab.IsPinned ? HorizontalAlignment.Center : HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = tab.IsPinned ? new Thickness(0) : new Thickness(0, 0, 10, 0)
                };
                var fallbackIcon = new TextBlock
                {
                    Text = tab.IsPdfView ? "\uEA90" : "\uE774",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 14,
                    Foreground = new SolidColorBrush(inactivePrivate ? Colors.White : fallbackColor),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                iconHost.Children.Add(fallbackIcon);
                if (Uri.TryCreate(tab.IconUrl, UriKind.Absolute, out Uri iconUri) &&
                    (iconUri.Scheme == Uri.UriSchemeHttp || iconUri.Scheme == Uri.UriSchemeHttps))
                {
                    var favicon = new Image
                    {
                        Width = 16,
                        Height = 16,
                        Stretch = Stretch.Uniform,
                        Source = new BitmapImage(iconUri)
                    };
                    favicon.ImageOpened += (sender, args) => fallbackIcon.Visibility = Visibility.Collapsed;
                    favicon.ImageFailed += (sender, args) => favicon.Visibility = Visibility.Collapsed;
                    iconHost.Children.Add(favicon);
                }
                Grid.SetColumn(iconHost, 0);
                if (tab.IsPinned) Grid.SetColumnSpan(iconHost, 3);

                var title = new TextBlock
                {
                    Text = (tab.IsPrivate ? "InPrivate  " : "") + (tab.Title ?? "New tab"),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 14,
                    Foreground = new SolidColorBrush(tabText),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0)
                };
                title.Visibility = tab.IsPinned ? Visibility.Collapsed : Visibility.Visible;
                Grid.SetColumn(title, 1);

                var close = new Button
                {
                    Content = "",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(tabText),
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Width = 40,
                    Height = 40,
                    Padding = new Thickness(0),
                    Tag = tab
                };
                close.Visibility = tab.IsPinned ? Visibility.Collapsed : Visibility.Visible;
                AutomationProperties.SetName(close, "Close " + (tab.Title ?? "tab"));
                close.Tapped += (sender, args) =>
                {
                    args.Handled = true;
                    CloseTab((BrowserTab)((FrameworkElement)sender).Tag);
                };
                Grid.SetColumn(close, 2);

                var dropCue = new Border
                {
                    Width = 2,
                    Background = new SolidColorBrush(Color.FromArgb(255, 0, 120, 215)),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Visibility = Visibility.Collapsed,
                    IsHitTestVisible = false
                };
                Grid.SetColumnSpan(dropCue, 3);

                header.Tapped += TabHeader_Tapped;
                header.PointerPressed += TabHeader_PointerPressed;
                header.PointerMoved += TabHeader_PointerMoved;
                header.PointerReleased += TabHeader_PointerReleased;
                header.PointerCanceled += TabHeader_PointerCanceled;
                header.PointerCaptureLost += TabHeader_PointerCaptureLost;
                header.PointerEntered += (sender, args) =>
                {
                    if (((FrameworkElement)sender).Tag != _currentTab &&
                        (!_tabDragActive || ((FrameworkElement)sender).Tag != _tabDragTab))
                    {
                        var hoveredTab = (BrowserTab)((FrameworkElement)sender).Tag;
                        var hoveredColor = hoveredTab.IsPrivate && !IsDarkTheme
                            ? Color.FromArgb(255, 103, 72, 123)
                            : hoverBackground;
                        ((Border)sender).Background = new SolidColorBrush(hoveredColor);
                    }
                };
                header.PointerExited += (sender, args) =>
                {
                    if (((FrameworkElement)sender).Tag != _currentTab &&
                        (!_tabDragActive || ((FrameworkElement)sender).Tag != _tabDragTab))
                        ((Border)sender).Background = new SolidColorBrush(Colors.Transparent);
                };
                ToolTipService.SetToolTip(header, string.IsNullOrWhiteSpace(tab.Address) ? tab.Title : tab.Address);
                AutomationProperties.SetName(header, (tab == _currentTab ? "Selected tab, " : "Tab, ") + (tab.Title ?? "New tab"));
                header.ContextFlyout = BuildTabContextMenu(tab);

                grid.Children.Add(iconHost);
                grid.Children.Add(title);
                grid.Children.Add(close);
                grid.Children.Add(dropCue);
                header.Child = grid;
                TabsPanel.Children.Add(header);
                _tabHeaders[tab] = header;
                _tabDropCues[tab] = dropCue;
            }

            if (TabPreviewBar?.Visibility == Visibility.Visible) RebuildTabPreviewCards(false);
        }

        private void TabHeader_Tapped(object sender, TappedRoutedEventArgs args)
        {
            var tab = (sender as FrameworkElement)?.Tag as BrowserTab;
            if (tab == null) return;
            if (ReferenceEquals(tab, _suppressTabTapFor))
            {
                args.Handled = true;
                _suppressTabTapFor = null;
                ++_tabTapSuppressionGeneration;
                return;
            }

            SwitchTab(tab);
        }

        private void TabHeader_PointerPressed(object sender, PointerRoutedEventArgs args)
        {
            var header = sender as Border;
            var tab = header?.Tag as BrowserTab;
            if (header == null || tab == null || !_tabs.Contains(tab)) return;

            var headerPoint = args.GetCurrentPoint(header);
            if (headerPoint.Properties.IsMiddleButtonPressed)
            {
                if (_tabDragPointer == null)
                {
                    args.Handled = true;
                    CloseTab(tab);
                }
                return;
            }

            if (_tabDragPointer != null || args.Pointer.PointerDeviceType != PointerDeviceType.Mouse ||
                !headerPoint.Properties.IsLeftButtonPressed || IsPointerInsideTabButton(args.OriginalSource as DependencyObject, header))
                return;

            var scrollerPoint = args.GetCurrentPoint(TabsScroller);
            _tabDragTab = tab;
            _tabDragHeader = header;
            _tabDragPointer = args.Pointer;
            _tabDragPressContentX = TabsScroller.HorizontalOffset + scrollerPoint.Position.X;
            _tabDragPressY = scrollerPoint.Position.Y;
            _tabDragLastScrollerX = scrollerPoint.Position.X;
            _tabDragLastY = scrollerPoint.Position.Y;
            _tabDragTargetIndex = _tabs.IndexOf(tab);
            _tabDragStructureVersion = _tabStructureVersion;
            _tabDragActive = false;

            if (!header.CapturePointer(args.Pointer))
            {
                _tabDragTab = null;
                _tabDragHeader = null;
                _tabDragPointer = null;
                _tabDragTargetIndex = -1;
            }
        }

        private void TabHeader_PointerMoved(object sender, PointerRoutedEventArgs args)
        {
            if (_tabDragPointer == null || args.Pointer.PointerId != _tabDragPointer.PointerId) return;
            var point = args.GetCurrentPoint(TabsScroller);
            if (!point.Properties.IsLeftButtonPressed)
            {
                EndTabPointerGesture(false, _tabDragActive, true, true);
                return;
            }

            _tabDragLastScrollerX = point.Position.X;
            _tabDragLastY = point.Position.Y;
            var contentX = TabsScroller.HorizontalOffset + _tabDragLastScrollerX;
            if (!_tabDragActive && Math.Abs(contentX - _tabDragPressContentX) < 8 && Math.Abs(_tabDragLastY - _tabDragPressY) < 8)
                return;

            if (!_tabDragActive)
            {
                _tabDragActive = true;
                _tabDragTransform = new TranslateTransform();
                _tabDragHeader.RenderTransform = _tabDragTransform;
                _tabDragHeader.Opacity = 0.86;
                Canvas.SetZIndex(_tabDragHeader, 1000);
            }

            if (!UpdateTabDragFromLastPointer())
            {
                EndTabPointerGesture(false, true, true, true);
                return;
            }
            args.Handled = true;
        }

        private void TabHeader_PointerReleased(object sender, PointerRoutedEventArgs args)
        {
            if (_tabDragPointer == null || args.Pointer.PointerId != _tabDragPointer.PointerId) return;
            var wasDrag = _tabDragActive;
            if (wasDrag)
            {
                var point = args.GetCurrentPoint(TabsScroller);
                _tabDragLastScrollerX = point.Position.X;
                _tabDragLastY = point.Position.Y;
                if (!UpdateTabDragFromLastPointer())
                {
                    EndTabPointerGesture(false, true, true, true);
                    args.Handled = true;
                    return;
                }
            }

            EndTabPointerGesture(wasDrag, wasDrag, true, true);
            if (wasDrag) args.Handled = true;
        }

        private void TabHeader_PointerCanceled(object sender, PointerRoutedEventArgs args)
        {
            if (_tabDragPointer == null || args.Pointer.PointerId != _tabDragPointer.PointerId) return;
            EndTabPointerGesture(false, true, false, true);
        }

        private void TabHeader_PointerCaptureLost(object sender, PointerRoutedEventArgs args)
        {
            if (_tabDragPointer == null || args.Pointer.PointerId != _tabDragPointer.PointerId) return;
            EndTabPointerGesture(false, true, false, true);
        }

        private static bool IsPointerInsideTabButton(DependencyObject source, DependencyObject header)
        {
            for (var element = source; element != null && element != header; element = VisualTreeHelper.GetParent(element))
                if (element is Button) return true;
            return false;
        }

        private bool UpdateTabDragFromLastPointer()
        {
            if (!_tabDragActive || _tabDragTab == null || _tabDragHeader == null || _tabDragTransform == null ||
                _tabStructureVersion != _tabDragStructureVersion || !_tabs.Contains(_tabDragTab))
                return false;

            var contentX = TabsScroller.HorizontalOffset + _tabDragLastScrollerX;
            _tabDragTransform.X = contentX - _tabDragPressContentX;

            if (_tabDragCue != null)
            {
                _tabDragCue.Visibility = Visibility.Collapsed;
                _tabDragCue = null;
            }

            var pinned = _tabDragTab.IsPinned;
            var peers = _tabs.Where(tab => tab != _tabDragTab && tab.IsPinned == pinned).ToList();
            var slot = 0;
            foreach (var peer in peers)
            {
                if (!_tabHeaders.TryGetValue(peer, out Border peerHeader)) return false;
                var origin = peerHeader.TransformToVisual(TabsPanel).TransformPoint(new Point(0, 0));
                if (contentX > origin.X + (peerHeader.ActualWidth / 2)) ++slot;
            }

            var groupStart = pinned ? 0 : _tabs.Count(tab => tab.IsPinned);
            _tabDragTargetIndex = groupStart + slot;
            if (peers.Count > 0)
            {
                var cueTab = slot < peers.Count ? peers[slot] : peers[peers.Count - 1];
                if (!_tabDropCues.TryGetValue(cueTab, out Border cue)) return false;
                cue.HorizontalAlignment = slot < peers.Count ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                cue.Visibility = Visibility.Visible;
                _tabDragCue = cue;
            }

            UpdateTabDragAutoScrollDirection();
            return true;
        }

        private void UpdateTabDragAutoScrollDirection()
        {
            var direction = 0;
            if (_tabDragActive && TabsScroller.ScrollableWidth > 0 &&
                _tabDragLastY >= -20 && _tabDragLastY <= TabsScroller.ActualHeight + 20)
            {
                if (_tabDragLastScrollerX < 28 && TabsScroller.HorizontalOffset > 0.5) direction = -1;
                else if (_tabDragLastScrollerX > TabsScroller.ActualWidth - 28 &&
                    TabsScroller.HorizontalOffset < TabsScroller.ScrollableWidth - 0.5) direction = 1;
            }

            _tabDragScrollDirection = direction;
            if (direction == 0) _tabDragAutoScrollTimer.Stop();
            else if (!_tabDragAutoScrollTimer.IsEnabled) _tabDragAutoScrollTimer.Start();
        }

        private void TabDragAutoScrollTimer_Tick(object sender, object e)
        {
            if (!_tabDragActive || _tabDragScrollDirection == 0 ||
                _tabStructureVersion != _tabDragStructureVersion)
            {
                _tabDragAutoScrollTimer.Stop();
                if (_tabDragPointer != null && _tabStructureVersion != _tabDragStructureVersion)
                    EndTabPointerGesture(false, true, true, true);
                return;
            }

            var next = Math.Max(0, Math.Min(TabsScroller.ScrollableWidth,
                TabsScroller.HorizontalOffset + (_tabDragScrollDirection * 12)));
            if (Math.Abs(next - TabsScroller.HorizontalOffset) < 0.5)
            {
                _tabDragScrollDirection = 0;
                _tabDragAutoScrollTimer.Stop();
                return;
            }
            TabsScroller.ChangeView(next, null, null, true);
        }

        private void TabsScroller_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (_tabDragActive && !UpdateTabDragFromLastPointer())
                EndTabPointerGesture(false, true, true, true);
        }

        private void EndTabPointerGesture(bool commit, bool consumeTap, bool releaseCapture, bool allowRebuild)
        {
            var pointer = _tabDragPointer;
            if (pointer == null)
            {
                if (!allowRebuild)
                {
                    _tabRebuildDeferred = false;
                    ++_tabDeferredRebuildGeneration;
                }
                return;
            }

            var tab = _tabDragTab;
            var header = _tabDragHeader;
            var targetIndex = _tabDragTargetIndex;
            var structureVersion = _tabDragStructureVersion;
            var wasDrag = _tabDragActive;

            _tabDragAutoScrollTimer.Stop();
            _tabDragScrollDirection = 0;
            if (_tabDragCue != null) _tabDragCue.Visibility = Visibility.Collapsed;
            if (header != null)
            {
                header.Opacity = 1;
                if (ReferenceEquals(header.RenderTransform, _tabDragTransform)) header.RenderTransform = null;
                Canvas.SetZIndex(header, 0);
            }

            _tabDragPointer = null;
            _tabDragTab = null;
            _tabDragHeader = null;
            _tabDragCue = null;
            _tabDragTransform = null;
            _tabDragTargetIndex = -1;
            _tabDragActive = false;

            if (consumeTap && tab != null) SuppressNextTabTap(tab);
            if (releaseCapture && header != null)
            {
                try { header.ReleasePointerCapture(pointer); }
                catch { }
            }

            if (!allowRebuild)
            {
                _tabRebuildDeferred = false;
                ++_tabDeferredRebuildGeneration;
                return;
            }

            if (commit && wasDrag && tab != null && structureVersion == _tabStructureVersion && _tabs.Contains(tab))
            {
                CommitTabReorder(tab, targetIndex);
                return;
            }

            if (wasDrag) RebuildTabs();
            else ScheduleDeferredTabRebuild();
        }

        private void CommitTabReorder(BrowserTab tab, int finalIndex)
        {
            var oldIndex = _tabs.IndexOf(tab);
            if (oldIndex >= 0)
            {
                var pinnedCount = _tabs.Count(candidate => candidate.IsPinned);
                var minimum = tab.IsPinned ? 0 : pinnedCount;
                var maximum = tab.IsPinned ? Math.Max(0, pinnedCount - 1) : _tabs.Count - 1;
                var target = Math.Max(minimum, Math.Min(maximum, finalIndex));
                if (target != oldIndex)
                {
                    _tabs.RemoveAt(oldIndex);
                    _tabs.Insert(target, tab);
                    ++_tabStructureVersion;
                    UpdateSessionSnapshot();
                }
            }
            RebuildTabs();
        }

        private void SuppressNextTabTap(BrowserTab tab)
        {
            _suppressTabTapFor = tab;
            var generation = ++_tabTapSuppressionGeneration;
            try
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    if (generation == _tabTapSuppressionGeneration && ReferenceEquals(_suppressTabTapFor, tab))
                        _suppressTabTapFor = null;
                });
            }
            catch { }
        }

        private void ScheduleDeferredTabRebuild()
        {
            if (!_tabRebuildDeferred) return;
            var generation = ++_tabDeferredRebuildGeneration;
            try
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    if (generation == _tabDeferredRebuildGeneration && _tabRebuildDeferred && _tabDragPointer == null)
                        RebuildTabs();
                });
            }
            catch { }
        }

        private void Navigate(BrowserTab tab, string input)
        {
            if (tab == null) return;
            ++tab.NavigationGeneration;
            CloseOmniboxPopup(false, true);
            if (tab.IsReadingView)
            {
                tab.IsReadingView = false;
                tab.OriginalAddress = null;
            }

            var requested = (input ?? string.Empty).Trim();
            if (requested.StartsWith("legacyedge-file:", StringComparison.OrdinalIgnoreCase))
            {
                _ = OpenStoredFileAsync(tab, requested.Substring("legacyedge-file:".Length));
                return;
            }

            var normalized = NormalizeAddress(requested);
            if (string.Equals(normalized, "about:blank", StringComparison.OrdinalIgnoreCase))
            {
                ShowStartPage(tab);
                return;
            }

            if (normalized.StartsWith("view-source:", StringComparison.OrdinalIgnoreCase))
            {
                _ = ViewSourceAsync(normalized.Substring("view-source:".Length), tab, tab);
                return;
            }

            if (Uri.TryCreate(normalized, UriKind.Absolute, out Uri documentUri) && LooksLikePdf(documentUri, null))
            {
                ClearLocalFileState(tab);
                _ = OpenPdfAsync(tab, documentUri);
                return;
            }

            ClearPdfState(tab);
            ClearLocalFileState(tab);

            try
            {
                tab.View.Navigate(new Uri(normalized));
            }
            catch
            {
                ShowTransientStatus("That address could not be opened.");
            }
        }

        private static string NormalizeAddress(string input)
        {
            var value = (input ?? string.Empty).Trim();
            if (value.Length == 0) return "about:blank";
            if (value.StartsWith("view-source:", StringComparison.OrdinalIgnoreCase)) return value;

            if (Uri.TryCreate(value, UriKind.Absolute, out Uri absolute))
                return absolute.ToString();

            if (!value.Contains(" ") && (value.Contains(".") || value.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)))
                return "http://" + value;

            return string.Format(BrowserDataStore.SearchTemplate, Uri.EscapeDataString(value));
        }

        private void WebView_NavigationStarting(WebView sender, WebViewNavigationStartingEventArgs args)
        {
            var tab = FindTab(sender);
            if (tab == null) return;
            ++tab.NavigationGeneration;
            if (_readAloudTab == tab && ReadAloudBar.Visibility == Visibility.Visible) StopReadAloud();
            tab.PreviewImage = null;
            tab.IsLoading = true;
            tab.LastNavigationSucceeded = false;
            _findHighlightedTabs.Remove(tab);
            if (tab == _currentTab && FindBar.Visibility == Visibility.Visible)
            {
                ++_findGeneration;
                _findQuery = string.Empty;
                _findMatchCount = 0;
                _findMatchIndex = 0;
                _findMatchCountTruncated = false;
                FindCountText.Text = "0 of 0";
            }
            if (args.Uri != null)
            {
                // Link/history navigation away from generated local or Reading
                // View content becomes an ordinary web page again.
                if (tab.LocalFile != null) ClearLocalFileState(tab);
                if (tab.IsReadingView)
                {
                    tab.IsReadingView = false;
                    tab.OriginalAddress = null;
                }
                var sourceAddress = string.Empty;
                var isSourceDocument = string.Equals(args.Uri.Scheme, "ms-local-stream", StringComparison.OrdinalIgnoreCase) &&
                    _sourceDocumentAddresses.TryGetValue(sender, out sourceAddress);
                if (!isSourceDocument) _sourceDocumentAddresses.Remove(sender);
                var nextAddress = isSourceDocument ? sourceAddress : args.Uri.ToString();
                if (!string.Equals(tab.Address, nextAddress, StringComparison.OrdinalIgnoreCase))
                {
                    tab.PreviousAddress = tab.Address;
                    tab.PreviousTitle = tab.Title;
                }
                tab.Address = nextAddress;
            }
            UpdateSessionSnapshot();
            if (tab == _currentTab) UpdateChrome();
            RebuildTabs();
            if (TabPreviewBar.Visibility == Visibility.Visible) RebuildTabPreviewCards(true);
        }

        private async void WebView_NavigationCompleted(WebView sender, WebViewNavigationCompletedEventArgs args)
        {
            var tab = FindTab(sender);
            if (tab == null) return;
            var viewGeneration = tab.ViewGeneration;
            tab.IsLoading = false;
            tab.LastNavigationSucceeded = args.IsSuccess;

            if (args.IsSuccess)
            {
                var title = sender.DocumentTitle;
                tab.Title = string.IsNullOrWhiteSpace(title) ? (tab.LocalFile?.Name ?? HostLabel(tab.Address)) : title;
                var iconUrl = await FindPageIconAsync(sender, tab.Address);
                if (!IsLiveWebView(tab, sender, viewGeneration)) return;
                tab.IconUrl = iconUrl;
                await ApplyZoomAsync(tab, sender);
                if (!IsLiveWebView(tab, sender, viewGeneration)) return;
                await InstallPageContextHookAsync(sender);
                if (!IsLiveWebView(tab, sender, viewGeneration)) return;

                if (!tab.IsPrivate && !tab.IsReadingView && IsWebAddress(tab.Address))
                    AddHistory(tab.Title, tab.Address, tab.IconUrl);
            }
            else
            {
                tab.Title = "Page unavailable";
                ShowTransientStatus("This page could not be loaded: " + args.WebErrorStatus);
            }

            if (tab == _currentTab)
            {
                UpdateChrome();
                if (args.IsSuccess && FindBar.Visibility == Visibility.Visible && !string.IsNullOrEmpty(FindBox.Text))
                    _findInitializationTask = InitializeFindAsync();
            }
            RebuildTabs();
        }

        private static async Task<string> FindPageIconAsync(WebView view, string address)
        {
            try
            {
                var script = "(function(){var links=document.querySelectorAll('link[rel]');for(var i=0;i<links.length;i++){var rel=(links[i].rel||'').toLowerCase();if(rel.indexOf('icon')!==-1&&links[i].href){return links[i].href;}}return '';})()";
                var discovered = await view.InvokeScriptAsync("eval", new[] { script });
                if (Uri.TryCreate(discovered, UriKind.Absolute, out Uri icon) &&
                    (icon.Scheme == Uri.UriSchemeHttp || icon.Scheme == Uri.UriSchemeHttps))
                    return icon.ToString();
            }
            catch { }

            if (Uri.TryCreate(address, UriKind.Absolute, out Uri page) &&
                (page.Scheme == Uri.UriSchemeHttp || page.Scheme == Uri.UriSchemeHttps))
                return new Uri(page, "/favicon.ico").ToString();
            return null;
        }

        private void WebView_NewWindowRequested(WebView sender, WebViewNewWindowRequestedEventArgs args)
        {
            var parent = FindTab(sender);
            CreateTab(args.Uri?.ToString(), true, parent != null && parent.IsPrivate);
            args.Handled = true;
        }

        private async void WebView_PermissionRequested(WebView sender, WebViewPermissionRequestedEventArgs args)
        {
            var tab = FindTab(sender);
            if (tab == null) return;
            var viewGeneration = tab.ViewGeneration;
            var request = args.PermissionRequest;
            request.Defer();
            var result = await ShowDialogAsync(new ContentDialog
            {
                Title = "Website permission",
                Content = (request.Uri?.Host ?? "This site") + " wants to use " + request.PermissionType + ".",
                PrimaryButtonText = "Allow",
                CloseButtonText = "Block",
                DefaultButton = ContentDialogButton.Close
            });

            if (!IsLiveWebView(tab, sender, viewGeneration)) return;
            var deferred = sender.DeferredPermissionRequestById(request.Id);
            if (deferred == null) return;
            if (result == ContentDialogResult.Primary) deferred.Allow(); else deferred.Deny();
        }

        private async void WebView_UnsupportedUriSchemeIdentified(WebView sender, WebViewUnsupportedUriSchemeIdentifiedEventArgs args)
        {
            if (args.Uri != null && string.Equals(args.Uri.Scheme, "legacyedge", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(args.Uri.Host, "newtab", StringComparison.OrdinalIgnoreCase))
            {
                args.Handled = true;
                var tab = FindTab(sender);
                var mode = args.Uri.AbsolutePath.Trim('/');
                if (tab != null && !tab.IsPrivate &&
                    (mode == "TopSitesAndSuggestedContent" || mode == "TopSites" || mode == "Blank"))
                {
                    BrowserDataStore.NewTabMode = mode;
                    ShowStartPage(tab);
                    ShowTransientStatus(mode == "Blank" ? "New tabs will be blank." : mode == "TopSites" ? "New tabs will show top sites." : "New tabs will show top sites and suggested content.");
                }
                return;
            }

            args.Handled = true;
            if (args.Uri == null) return;
            var scheme = args.Uri.Scheme ?? string.Empty;
            if (new[] { "file", "javascript", "data", "about", "ms-appx", "ms-appdata", "ms-settings", "legacyedge" }
                .Any(item => string.Equals(item, scheme, StringComparison.OrdinalIgnoreCase)))
            {
                ShowTransientStatus("This page isn't allowed to open that protocol.");
                return;
            }

            var source = FindTab(sender)?.Address;
            var sourceLabel = Uri.TryCreate(source, UriKind.Absolute, out Uri sourceUri) && !string.IsNullOrWhiteSpace(sourceUri.Host)
                ? sourceUri.Host
                : "This page";
            var result = await ShowDialogAsync(new ContentDialog
            {
                Title = "Open another app?",
                Content = sourceLabel + " wants to open a link using the " + scheme + " protocol.",
                PrimaryButtonText = "Open",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            });
            if (result != ContentDialogResult.Primary) return;
            var launched = await Launcher.LaunchUriAsync(args.Uri);
            if (!launched) ShowTransientStatus("No installed app can open this link.");
        }

        private async void WebView_UnviewableContentIdentified(WebView sender, WebViewUnviewableContentIdentifiedEventArgs args)
        {
            var tab = FindTab(sender);
            if (tab != null && LooksLikePdf(args.Uri, args.MediaType))
                await OpenPdfAsync(tab, args.Uri);
            else
                await DownloadUriAsync(args.Uri);
        }

        private void WebView_ContainsFullScreenElementChanged(WebView sender, object args)
        {
            if (FindTab(sender) != _currentTab) return;
            if (sender.ContainsFullScreenElement) EnterFullScreen(); else ExitFullScreen();
        }

        private async void WebView_LongRunningScriptDetected(WebView sender, WebViewLongRunningScriptDetectedEventArgs args)
        {
            if (args.ExecutionTime.TotalSeconds < 10) return;
            var tab = FindTab(sender);
            if (tab == null) return;
            var viewGeneration = tab.ViewGeneration;
            var result = await ShowDialogAsync(new ContentDialog
            {
                Title = "This page is not responding",
                Content = "A script on this page has been running for a long time.",
                PrimaryButtonText = "Stop script",
                CloseButtonText = "Keep waiting"
            });
            if (!IsLiveWebView(tab, sender, viewGeneration)) return;
            args.StopPageScriptExecution = result == ContentDialogResult.Primary;
        }

        private static async Task InstallPageContextHookAsync(WebView view)
        {
            if (view == null) return;
            const string script = "(function(){if(window.__legacyEdgeContextInstalled)return 'ready';window.__legacyEdgeContextInstalled=true;window.__legacyEdgeContextData='';document.addEventListener('contextmenu',function(e){function closest(n,t){while(n&&n.tagName!==t)n=n.parentElement;return n;}var a=closest(e.target,'A');var i=closest(e.target,'IMG');var d=e.target;while(d&&!(d.tagName==='INPUT'||d.tagName==='TEXTAREA'||d.isContentEditable))d=d.parentElement;var s=window.getSelection?window.getSelection().toString():'';if(d&&typeof d.selectionStart==='number')s=(d.value||'').substring(d.selectionStart,d.selectionEnd);window.__legacyEdgeContextData=JSON.stringify({link:a&&a.href?a.href:'',image:i&&i.src?i.src:'',text:s||'',editable:!!d,x:e.clientX,y:e.clientY});e.preventDefault();},true);return 'ready';})()";
            try { await view.InvokeScriptAsync("eval", new[] { script }); }
            catch { }
        }

        private async void ContextPollTimer_Tick(object sender, object e)
        {
            var tab = _currentTab;
            if (_contextPollBusy || tab?.View == null || tab.IsLoading || tab.View.Visibility != Visibility.Visible) return;
            var view = tab.View;
            var viewGeneration = tab.ViewGeneration;
            _contextPollBusy = true;
            try
            {
                const string pollScript = "(function(){if(typeof window.__legacyEdgeContextData==='undefined')return '__missing__';var d=window.__legacyEdgeContextData||'';window.__legacyEdgeContextData='';return d;})()";
                var raw = await view.InvokeScriptAsync("eval", new[] { pollScript });
                if (!IsLiveWebView(tab, view, viewGeneration) || tab != _currentTab) return;
                if (string.Equals(raw, "__missing__", StringComparison.Ordinal))
                {
                    await InstallPageContextHookAsync(view);
                    return;
                }
                if (string.IsNullOrWhiteSpace(raw) || !JsonObject.TryParse(raw, out JsonObject context)) return;

                _contextTab = tab;
                _contextLinkUrl = context.GetNamedString("link", string.Empty);
                _contextImageUrl = context.GetNamedString("image", string.Empty);
                _contextSelectedText = context.GetNamedString("text", string.Empty).Trim();
                _contextIsEditable = context.GetNamedBoolean("editable", false);

                var zoomFactor = Math.Max(0.25, tab.ZoomPercent / 100d);
                var x = Math.Max(8, Math.Min(view.ActualWidth - 8, context.GetNamedNumber("x", 12) * zoomFactor));
                var y = Math.Max(8, Math.Min(view.ActualHeight - 8, context.GetNamedNumber("y", 12) * zoomFactor));
                BuildPageContextMenu(view).ShowAt(ContentArea, new Point(x, y));
            }
            catch { }
            finally { _contextPollBusy = false; }
        }

        private async void OpenKeyboardPageContextMenu()
        {
            var tab = _currentTab;
            var view = tab?.View;
            if (view == null) return;
            var viewGeneration = tab.ViewGeneration;
            try
            {
                await InstallPageContextHookAsync(view);
                if (!IsLiveWebView(tab, view, viewGeneration) || tab != _currentTab) return;
                const string script = "(function(){var t=document.activeElement||document.body;var r=t.getBoundingClientRect?t.getBoundingClientRect():null;var x=r&&r.width?Math.round(r.left+Math.min(r.width/2,240)):Math.round(window.innerWidth*.4);var y=r&&r.height?Math.round(r.top+Math.min(r.height/2,160)):Math.round(window.innerHeight*.35);x=Math.max(12,Math.min(window.innerWidth-12,x));y=Math.max(12,Math.min(window.innerHeight-12,y));var e=document.createEvent('MouseEvents');e.initMouseEvent('contextmenu',true,true,window,1,x,y,x,y,false,false,false,false,2,null);t.dispatchEvent(e);})()";
                await view.InvokeScriptAsync("eval", new[] { script });
            }
            catch { ShowTransientStatus("The page context menu is not available here."); }
        }

        private MenuFlyout BuildPageContextMenu(WebView view)
        {
            var menu = new MenuFlyout
            {
                MenuFlyoutPresenterStyle = Resources["PageContextMenuPresenterStyle"] as Style
            };

            if (_contextIsEditable)
            {
                var canPaste = false;
                try { canPaste = Clipboard.GetContent().Contains(StandardDataFormats.Text); }
                catch { }
                menu.Items.Add(CreatePageContextItem("Undo", ContextUndo_Click));
                menu.Items.Add(CreatePageContextItem("Cut", ContextCut_Click, !string.IsNullOrWhiteSpace(_contextSelectedText)));
                menu.Items.Add(CreatePageContextItem("Copy", ContextCopySelection_Click, !string.IsNullOrWhiteSpace(_contextSelectedText)));
                menu.Items.Add(CreatePageContextItem("Paste", ContextPaste_Click, canPaste));
                menu.Items.Add(CreatePageContextItem("Delete", ContextDelete_Click, !string.IsNullOrWhiteSpace(_contextSelectedText)));
                menu.Items.Add(CreatePageContextItem("Select all", ContextSelectAll_Click));
                menu.Items.Add(new MenuFlyoutSeparator());
            }

            if (IsWebAddress(_contextLinkUrl))
            {
                menu.Items.Add(CreatePageContextItem("Open link in new tab", ContextOpenLink_Click));
                menu.Items.Add(CreatePageContextItem("Open link in new window", ContextOpenWindowLink_Click));
                menu.Items.Add(CreatePageContextItem("Open link in new InPrivate window", ContextOpenPrivateLink_Click));
                menu.Items.Add(CreatePageContextItem("Save link as", ContextSaveLink_Click));
                menu.Items.Add(CreatePageContextItem("Copy link", ContextCopyLink_Click));
                menu.Items.Add(new MenuFlyoutSeparator());
            }

            if (IsWebAddress(_contextImageUrl))
            {
                menu.Items.Add(CreatePageContextItem("Open picture in new tab", ContextOpenImage_Click));
                menu.Items.Add(CreatePageContextItem("Save picture as", ContextSaveImage_Click));
                menu.Items.Add(CreatePageContextItem("Copy picture address", ContextCopyImage_Click));
                menu.Items.Add(new MenuFlyoutSeparator());
            }

            if (!string.IsNullOrWhiteSpace(_contextSelectedText))
            {
                var preview = string.Join(" ", _contextSelectedText.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
                if (preview.Length > 34) preview = preview.Substring(0, 34) + "\u2026";
                if (!_contextIsEditable) menu.Items.Add(CreatePageContextItem("Copy", ContextCopySelection_Click));
                menu.Items.Add(CreatePageContextItem("Search the web for \"" + preview + "\"", ContextSearchSelection_Click));
                menu.Items.Add(CreatePageContextItem("Read aloud selection", ContextReadAloudSelection_Click));
                menu.Items.Add(new MenuFlyoutSeparator());
            }

            menu.Items.Add(CreatePageContextItem("Back", ContextBack_Click, view.CanGoBack));
            menu.Items.Add(CreatePageContextItem("Forward", ContextForward_Click, view.CanGoForward));
            menu.Items.Add(CreatePageContextItem("Refresh", ContextRefresh_Click));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(CreatePageContextItem("Save page as", ContextSavePage_Click, IsWebAddress(_contextTab?.Address)));
            menu.Items.Add(CreatePageContextItem("Print", ContextPrint_Click));
            menu.Items.Add(CreatePageContextItem("Read aloud", ContextReadAloud_Click));
            if (!_contextIsEditable) menu.Items.Add(CreatePageContextItem("Select all", ContextSelectAll_Click));
            menu.Items.Add(CreatePageContextItem("View source", ContextViewSource_Click, IsWebAddress(_contextTab?.Address)));
            return menu;
        }

        private MenuFlyoutItem CreatePageContextItem(string text, RoutedEventHandler handler, bool enabled = true)
        {
            var item = new MenuFlyoutItem
            {
                Text = text,
                IsEnabled = enabled,
                Style = Resources["PageContextMenuItemStyle"] as Style
            };
            item.Click += handler;
            return item;
        }

        private void ContextOpenLink_Click(object sender, RoutedEventArgs e) => CreateTab(_contextLinkUrl, true, _contextTab?.IsPrivate == true);
        private async void ContextOpenWindowLink_Click(object sender, RoutedEventArgs e)
        {
            if (!await OpenNewWindowAsync(_contextLinkUrl, _contextTab?.IsPrivate == true))
                ShowTransientStatus("A new browser window could not be opened.");
        }
        private async void ContextOpenPrivateLink_Click(object sender, RoutedEventArgs e)
        {
            if (!await OpenNewWindowAsync(_contextLinkUrl, true))
                ShowTransientStatus("A new InPrivate window could not be opened.");
        }
        private async void ContextSaveLink_Click(object sender, RoutedEventArgs e)
        {
            if (Uri.TryCreate(_contextLinkUrl, UriKind.Absolute, out Uri uri)) await DownloadUriAsync(uri);
        }
        private void ContextCopyLink_Click(object sender, RoutedEventArgs e) => CopyContextText(_contextLinkUrl, "Link copied.");
        private void ContextOpenImage_Click(object sender, RoutedEventArgs e) => CreateTab(_contextImageUrl, true, _contextTab?.IsPrivate == true);
        private async void ContextSaveImage_Click(object sender, RoutedEventArgs e)
        {
            if (Uri.TryCreate(_contextImageUrl, UriKind.Absolute, out Uri uri)) await DownloadUriAsync(uri);
        }
        private void ContextCopyImage_Click(object sender, RoutedEventArgs e) => CopyContextText(_contextImageUrl, "Picture address copied.");
        private void ContextCopySelection_Click(object sender, RoutedEventArgs e) => CopyContextText(_contextSelectedText, "Text copied.");
        private async void ContextUndo_Click(object sender, RoutedEventArgs e) => await ExecuteContextEditCommandAsync("undo");
        private async void ContextCut_Click(object sender, RoutedEventArgs e)
        {
            CopyContextText(_contextSelectedText, "Text cut.");
            await ExecuteContextEditCommandAsync("delete");
        }
        private async void ContextDelete_Click(object sender, RoutedEventArgs e) => await ExecuteContextEditCommandAsync("delete");
        private async void ContextPaste_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var content = Clipboard.GetContent();
                if (!content.Contains(StandardDataFormats.Text)) return;
                var text = await content.GetTextAsync();
                var escaped = EscapeJavaScript(text);
                var script = "(function(){var e=document.activeElement,t='" + escaped + "';if(!e)return false;if(typeof e.selectionStart==='number'){var s=e.selectionStart,n=e.selectionEnd;e.value=e.value.slice(0,s)+t+e.value.slice(n);e.selectionStart=e.selectionEnd=s+t.length;var v=document.createEvent('Event');v.initEvent('input',true,true);e.dispatchEvent(v);return true;}if(e.isContentEditable){document.execCommand('insertText',false,t);return true;}return false;})()";
                if (_contextTab != null) await _contextTab.View.InvokeScriptAsync("eval", new[] { script });
            }
            catch { ShowTransientStatus("Clipboard text could not be pasted."); }
        }
        private void ContextSearchSelection_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_contextSelectedText))
                CreateTab(NormalizeAddress(_contextSelectedText), true, _contextTab?.IsPrivate == true);
        }
        private async void ContextReadAloudSelection_Click(object sender, RoutedEventArgs e) => await StartReadAloudAsync(_contextSelectedText);
        private async void ContextReadAloud_Click(object sender, RoutedEventArgs e) => await StartReadAloudAsync(null);
        private void ContextBack_Click(object sender, RoutedEventArgs e) { if (_contextTab?.View.CanGoBack == true) _contextTab.View.GoBack(); }
        private void ContextForward_Click(object sender, RoutedEventArgs e) { if (_contextTab?.View.CanGoForward == true) _contextTab.View.GoForward(); }
        private void ContextRefresh_Click(object sender, RoutedEventArgs e) => _contextTab?.View.Refresh();
        private void ContextSavePage_Click(object sender, RoutedEventArgs e) => SavePage_Click(sender, e);
        private void ContextPrint_Click(object sender, RoutedEventArgs e) => Print_Click(sender, e);
        private async void ContextSelectAll_Click(object sender, RoutedEventArgs e)
        {
            try { if (_contextTab != null) await _contextTab.View.InvokeScriptAsync("eval", new[] { "document.execCommand('selectAll');" }); }
            catch { ShowTransientStatus("This page cannot be selected."); }
        }
        private void ContextViewSource_Click(object sender, RoutedEventArgs e) => ViewSource_Click(sender, e);

        private async Task ExecuteContextEditCommandAsync(string command)
        {
            try
            {
                if (_contextTab != null)
                    await _contextTab.View.InvokeScriptAsync("eval", new[] { "document.execCommand('" + EscapeJavaScript(command) + "');" });
            }
            catch { ShowTransientStatus("That editing command is not available here."); }
        }

        private void CopyContextText(string value, string confirmation)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            try
            {
                var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                package.SetText(value);
                Clipboard.SetContent(package);
                Clipboard.Flush();
                ShowTransientStatus(confirmation);
            }
            catch { ShowTransientStatus("The clipboard is unavailable."); }
        }

        private void UpdateChrome()
        {
            if (_currentTab == null) return;
            var isPdf = _currentTab.IsPdfView;
            BackButton.IsEnabled = isPdf || _currentTab.View.CanGoBack;
            ForwardButton.IsEnabled = !isPdf && _currentTab.View.CanGoForward;
            RefreshButton.Content = _currentTab.IsLoading && !isPdf ? "" : "";
            LoadingProgress.Visibility = _currentTab.IsLoading ? Visibility.Visible : Visibility.Collapsed;
            if (!_omniboxEditing) SetAddressBoxText(_currentTab.Address ?? string.Empty);
            var chromeText = IsDarkTheme ? Colors.White : Color.FromArgb(255, 34, 34, 34);
            var secondaryText = IsDarkTheme ? Color.FromArgb(255, 230, 230, 230) : Color.FromArgb(255, 96, 96, 96);
            if (_currentTab.LocalFile != null)
            {
                SecurityButton.Content = "\uE8A5";
                SecurityButton.Foreground = new SolidColorBrush(secondaryText);
            }
            else
            {
                var isHttps = Uri.TryCreate(_currentTab.Address, UriKind.Absolute, out Uri securityUri) &&
                    string.Equals(securityUri.Scheme, "https", StringComparison.OrdinalIgnoreCase);
                SecurityButton.Content = isHttps && _currentTab.LastNavigationSucceeded
                    ? "\uE72E"
                    : isHttps ? "\uE946" : "\uE7BA";
                SecurityButton.Foreground = new SolidColorBrush(secondaryText);
            }
            ReadingViewButton.Foreground = _currentTab.IsReadingView ? new SolidColorBrush(Color.FromArgb(255, 0, 120, 215)) : new SolidColorBrush(chromeText);
            ReadingViewButton.IsEnabled = !isPdf && IsWebAddress(_currentTab.Address);
            FavoriteButton.Content = IsFavorite(_currentTab.Address) ? "" : "";
            FavoriteButton.Foreground = IsFavorite(_currentTab.Address) ? new SolidColorBrush(Color.FromArgb(255, 0, 120, 215)) : new SolidColorBrush(chromeText);
            SecurityButton.IsEnabled = _currentTab.LocalFile != null || !string.IsNullOrWhiteSpace(_currentTab.Address);
            FavoriteButton.IsEnabled = IsWebAddress(_currentTab.Address);
            ZoomLabelButton.Content = isPdf ? ((int)Math.Round(_currentTab.PdfZoom * 100)) + "%" : ((int)_currentTab.ZoomPercent) + "%";
            ApplyTitleBarTheme(_currentTab.IsPrivate);
            ApplicationView.GetForCurrentView().Title = (_currentTab.Title ?? "New tab") + (_currentTab.IsPrivate ? " - InPrivate" : "");
        }

        private void AddHistory(string title, string url, string iconUrl)
        {
            ReplaceItems(_history, BrowserDataStore.RecordHistory(title, url, iconUrl));
        }

        private static void ReplaceItems(ObservableCollection<BrowserItem> target, IEnumerable<BrowserItem> values)
        {
            if (target == null) return;
            target.Clear();
            foreach (var value in values ?? Enumerable.Empty<BrowserItem>()) target.Add(value);
        }

        private bool IsFavorite(string address) => !string.IsNullOrWhiteSpace(address) && _favorites != null && _favorites.Any(item => string.Equals(item.Url, address, StringComparison.OrdinalIgnoreCase));

        private void RebuildFavoritesBar()
        {
            if (FavoritesBarItems == null || _favorites == null) return;
            FavoritesBarItems.Children.Clear();
            foreach (var favorite in _favorites.Where(item => item != null && (string.IsNullOrWhiteSpace(item.Folder) || string.Equals(item.Folder, "FavoritesBar", StringComparison.OrdinalIgnoreCase))))
            {
                var title = string.IsNullOrWhiteSpace(favorite.Title) ? HostLabel(favorite.Url) : favorite.Title.Trim();
                if (string.IsNullOrWhiteSpace(title) || string.Equals(title, "New tab", StringComparison.OrdinalIgnoreCase)) title = "Favorite";

                var content = new Grid
                {
                    Margin = new Thickness(10, 0, 10, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                };
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var iconHost = new Grid
                {
                    Width = 16,
                    Height = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var fallbackIcon = new FontIcon
                {
                    Glyph = "\uE774",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 14,
                    Foreground = new SolidColorBrush(IsDarkTheme ? Color.FromArgb(255, 196, 196, 196) : Color.FromArgb(255, 70, 70, 70)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                iconHost.Children.Add(fallbackIcon);
                if (Uri.TryCreate(favorite.IconUrl, UriKind.Absolute, out Uri iconUri) &&
                    (iconUri.Scheme == Uri.UriSchemeHttp || iconUri.Scheme == Uri.UriSchemeHttps))
                {
                    try
                    {
                        var favicon = new Image
                        {
                            Width = 16,
                            Height = 16,
                            Stretch = Stretch.Uniform,
                            Source = new BitmapImage(iconUri)
                        };
                        favicon.ImageOpened += (sender, args) => fallbackIcon.Visibility = Visibility.Collapsed;
                        favicon.ImageFailed += (sender, args) =>
                        {
                            favicon.Visibility = Visibility.Collapsed;
                            fallbackIcon.Visibility = Visibility.Visible;
                        };
                        iconHost.Children.Add(favicon);
                    }
                    catch
                    {
                        fallbackIcon.Visibility = Visibility.Visible;
                    }
                }
                Grid.SetColumn(iconHost, 0);

                var titleBlock = new TextBlock
                {
                    Text = title,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                Grid.SetColumn(titleBlock, 1);
                content.Children.Add(iconHost);
                content.Children.Add(titleBlock);

                var button = new Button
                {
                    Content = content,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Width = 176,
                    Height = 32,
                    Padding = new Thickness(0),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Stretch,
                    Tag = favorite
                };
                button.Click += (sender, args) =>
                {
                    if (_currentTab != null && (sender as FrameworkElement)?.Tag is BrowserItem item && !string.IsNullOrWhiteSpace(item.Url)) Navigate(_currentTab, item.Url);
                };
                AutomationProperties.SetName(button, "Open favorite " + title);
                ToolTipService.SetToolTip(button, string.IsNullOrWhiteSpace(favorite.Url) ? title : favorite.Url);
                FavoritesBarItems.Children.Add(button);
            }
        }

        private void NewTab_Click(object sender, RoutedEventArgs e)
        {
            MoreMenu.Hide();
            CreateTab();
            FocusAddressBar();
        }
        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTab?.IsPdfView == true)
            {
                var tab = _currentTab;
                var returnAddress = tab.PdfReturnAddress;
                var returnToStartPage = string.IsNullOrWhiteSpace(returnAddress);
                var reloadReturnPage = tab.PdfReturnNeedsReload;
                var wasLocalFile = !string.IsNullOrWhiteSpace(tab.LocalFileToken);
                ClearPdfState(tab);
                if (wasLocalFile) ClearLocalFileState(tab);
                if (returnToStartPage)
                {
                    ShowStartPage(tab);
                    return;
                }
                if (reloadReturnPage)
                {
                    Navigate(tab, returnAddress);
                    return;
                }
                tab.View.Visibility = Visibility.Visible;
                UpdateChrome();
                RebuildTabs();
                UpdateSessionSnapshot();
            }
            else if (_currentTab?.View.CanGoBack == true) _currentTab.View.GoBack();
        }
        private void Forward_Click(object sender, RoutedEventArgs e) { if (_currentTab?.IsPdfView != true && _currentTab?.View.CanGoForward == true) _currentTab.View.GoForward(); }
        private void Home_Click(object sender, RoutedEventArgs e) => Navigate(_currentTab, BrowserDataStore.HomePage);
        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTab == null) return;
            if (_currentTab.IsPdfView)
            {
                if (!_currentTab.IsLoading && _currentTab.LocalFile != null) _ = OpenLocalPdfAsync(_currentTab, _currentTab.LocalFile);
                else if (!_currentTab.IsLoading && Uri.TryCreate(_currentTab.PdfSourceAddress, UriKind.Absolute, out Uri uri)) _ = OpenPdfAsync(_currentTab, uri);
            }
            else if (_currentTab.LocalFile != null && !_currentTab.IsLoading) ReloadTab(_currentTab);
            else if (_currentTab.IsLoading) _currentTab.View.Stop();
            else _currentTab.View.Refresh();
        }

        private void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Down)
            {
                MoveOmniboxSelection(1);
                e.Handled = true;
                return;
            }
            if (e.Key == VirtualKey.Up)
            {
                MoveOmniboxSelection(-1);
                e.Handled = true;
                return;
            }
            if (e.Key == VirtualKey.Escape)
            {
                if (OmniboxPopup.IsOpen)
                    DismissOmniboxSelectionPreview();
                else
                {
                    CloseOmniboxPopup(true, true);
                    FocusCurrentContent();
                }
                e.Handled = true;
                return;
            }
            if (e.Key == VirtualKey.Delete)
            {
                var shift = (Window.Current.CoreWindow.GetKeyState(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;
                if (shift && OmniboxList.SelectedItem is OmniboxSuggestion historySuggestion && historySuggestion.CanDelete)
                {
                    DeleteHistorySuggestion(historySuggestion);
                    e.Handled = true;
                }
                return;
            }
            if (e.Key != VirtualKey.Enter) return;

            e.Handled = true;
            var window = Window.Current.CoreWindow;
            var control = (window.GetKeyState(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
            var alt = (window.GetKeyState(VirtualKey.Menu) & CoreVirtualKeyStates.Down) != 0;
            if (!control && OmniboxList.SelectedItem is OmniboxSuggestion suggestion)
            {
                ActivateOmniboxSuggestion(suggestion);
                return;
            }

            var value = AddressBox.Text;
            if (control && !string.IsNullOrWhiteSpace(value) && value.IndexOfAny(new[] { ' ', '/', ':', '.' }) < 0)
                value = "www." + value.Trim() + ".com";

            CloseOmniboxPopup(false, true);
            if (alt)
                CreateTab(value, true, _currentTab?.IsPrivate == true);
            else
                Navigate(_currentTab, value);
            FocusCurrentContent();
        }

        private void AddressBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (SidePanePopup.IsOpen && !_hubPinned) SidePanePopup.IsOpen = false;
            UpdateAddressBoxFocusColors(true);
            _omniboxEditing = true;
            _omniboxOriginalText = _currentTab?.Address ?? string.Empty;
            _omniboxTypedText = AddressBox.Text ?? string.Empty;
            AddressBox.SelectAll();
            RefreshOmniboxSuggestions(AddressBox.Text);
        }

        private void FocusAddressBar()
        {
            AddressBox.Focus(FocusState.Programmatic);
            _omniboxEditing = true;
            _omniboxOriginalText = _currentTab?.Address ?? string.Empty;
            _omniboxTypedText = AddressBox.Text ?? string.Empty;
            AddressBox.SelectAll();
            RefreshOmniboxSuggestions(AddressBox.Text);
        }

        private void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressOmniboxTextChanged || !_omniboxEditing) return;
            _omniboxTypedText = AddressBox.Text ?? string.Empty;
            RefreshOmniboxSuggestions(AddressBox.Text);
        }

        private async void AddressBox_LostFocus(object sender, RoutedEventArgs e)
        {
            await Task.Delay(120);
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (IsInsideOmnibox(focused)) return;
            CloseOmniboxPopup(true, true);
            UpdateAddressBoxFocusColors(false);
        }

        private void UpdateAddressBoxFocusColors(bool focused)
        {
            if (AddressBox == null) return;
            var darkFocused = IsDarkTheme && focused;
            AddressBox.Foreground = new SolidColorBrush(darkFocused
                ? Color.FromArgb(255, 27, 27, 27)
                : IsDarkTheme ? Colors.White : Color.FromArgb(255, 27, 27, 27));
            AddressBox.PlaceholderForeground = new SolidColorBrush(darkFocused
                ? Color.FromArgb(255, 95, 95, 95)
                : IsDarkTheme ? Color.FromArgb(255, 190, 190, 190) : Color.FromArgb(255, 102, 102, 102));
        }

        private void OmniboxList_ItemClick(object sender, ItemClickEventArgs e)
        {
            ActivateOmniboxSuggestion(e.ClickedItem as OmniboxSuggestion);
        }

        private void AddressBarHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (OmniboxPopup.IsOpen) PositionOmniboxPopup();
        }

        private void RefreshOmniboxSuggestions(string input)
        {
            if (!_omniboxEditing)
            {
                _omniboxSuggestions.Clear();
                OmniboxPopup.IsOpen = false;
                return;
            }

            var query = (input ?? string.Empty).Trim();
            var candidates = new List<OmniboxSuggestion>();
            var currentPrivate = _currentTab?.IsPrivate == true;

            for (var index = 0; index < _tabs.Count; index++)
            {
                var tab = _tabs[index];
                if (tab == _currentTab || tab.IsPrivate != currentPrivate || string.IsNullOrWhiteSpace(tab.Address)) continue;
                var match = ScoreOmniboxMatch(query, tab.Title, tab.Address);
                if (match < 0) continue;
                candidates.Add(new OmniboxSuggestion
                {
                    Title = DisplayOmniboxAddress(tab.Address),
                    Detail = "Open tab  ·  " + tab.Address,
                    Address = tab.Address,
                    Glyph = "\uE737",
                    Kind = "tab",
                    ActionLabel = (string.IsNullOrWhiteSpace(tab.Title) ? HostLabel(tab.Address) : tab.Title) + "  ·  Switch to tab",
                    TabId = tab.Id,
                    Rank = 30000 + match - index
                });
            }

            if (_favorites != null)
            {
                for (var index = 0; index < _favorites.Count; index++)
                {
                    var favorite = _favorites[index];
                    if (string.IsNullOrWhiteSpace(favorite.Url)) continue;
                    var match = ScoreOmniboxMatch(query, favorite.Title, favorite.Url);
                    if (match < 0) continue;
                    candidates.Add(new OmniboxSuggestion
                    {
                        Title = DisplayOmniboxAddress(favorite.Url),
                        Detail = "Favorite  ·  " + favorite.Url,
                        Address = favorite.Url,
                        Glyph = "\uE734",
                        Kind = "favorite",
                        ActionLabel = string.IsNullOrWhiteSpace(favorite.Title) ? "Favorite" : favorite.Title,
                        Rank = 20000 + match - index
                    });
                }
            }

            if (!currentPrivate && _history != null)
            {
                for (var index = 0; index < _history.Count; index++)
                {
                    var history = _history[index];
                    if (string.IsNullOrWhiteSpace(history.Url)) continue;
                    var match = ScoreOmniboxMatch(query, history.Title, history.Url);
                    if (match < 0) continue;
                    candidates.Add(new OmniboxSuggestion
                    {
                        Title = DisplayOmniboxAddress(history.Url),
                        Detail = "History  ·  " + history.Url,
                        Address = history.Url,
                        Glyph = "\uE81C",
                        Kind = "history",
                        ActionLabel = string.IsNullOrWhiteSpace(history.Title) ? "History" : history.Title,
                        Rank = 10000 + match - index,
                        CanDelete = true
                    });
                }
            }

            if (LooksLikeDirectAddressInput(query))
            {
                var target = NormalizeAddress(query);
                candidates.Add(new OmniboxSuggestion
                {
                    Title = query,
                    Detail = target,
                    Address = target,
                    Glyph = "\uE774",
                    Kind = "website",
                    ActionLabel = "Go to website",
                    Rank = 29000
                });
            }

            var ordered = candidates
                .OrderByDescending(candidate => candidate.Rank)
                .ThenBy(candidate => candidate.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var usedAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasSearchAction = query.Length > 0 && BrowserDataStore.ShowSearchSuggestions;
            var isDirectInput = LooksLikeDirectAddressInput(query);
            OmniboxSuggestion searchAction = null;
            if (hasSearchAction)
            {
                var provider = SearchProviderFromTemplate(BrowserDataStore.SearchTemplate);
                searchAction = new OmniboxSuggestion
                {
                    Title = query,
                    Detail = "Search the web for \"" + query + "\"",
                    Address = string.Format(BrowserDataStore.SearchTemplate, Uri.EscapeDataString(query)),
                    Glyph = "\uE721",
                    Kind = "search",
                    ActionLabel = provider + " search",
                    Rank = 0
                };
            }

            _omniboxSuggestions.Clear();
            if (searchAction != null && !isDirectInput) _omniboxSuggestions.Add(searchAction);
            var resultLimit = hasSearchAction ? 7 : 8;
            var localResultCount = 0;
            foreach (var candidate in ordered)
            {
                var key = OmniboxAddressKey(candidate.Address);
                if (key.Length > 0 && !usedAddresses.Add(key)) continue;
                _omniboxSuggestions.Add(candidate);
                localResultCount++;
                if (localResultCount >= resultLimit) break;
            }
            if (searchAction != null && isDirectInput) _omniboxSuggestions.Add(searchAction);

            OmniboxList.SelectedIndex = query.Length > 0 ? 0 : -1;
            if (_omniboxSuggestions.Count == 0)
            {
                OmniboxPopup.IsOpen = false;
                return;
            }

            PositionOmniboxPopup();
            OmniboxPopup.IsOpen = true;
        }

        private static int ScoreOmniboxMatch(string query, string title, string address)
        {
            if (query.Length == 0) return 0;
            title = title ?? string.Empty;
            address = address ?? string.Empty;
            var searchable = title + " " + address;
            foreach (var token in query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (searchable.IndexOf(token, StringComparison.CurrentCultureIgnoreCase) < 0) return -1;
            }

            var score = 0;
            if (string.Equals(address, query, StringComparison.OrdinalIgnoreCase)) score += 1600;
            else if (address.StartsWith(query, StringComparison.OrdinalIgnoreCase)) score += 900;
            else if (address.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) score += 450;

            if (string.Equals(title, query, StringComparison.CurrentCultureIgnoreCase)) score += 1400;
            else if (title.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)) score += 800;
            else if (title.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0) score += 350;

            if (Uri.TryCreate(address, UriKind.Absolute, out Uri uri))
            {
                var host = uri.Host ?? string.Empty;
                if (string.Equals(host, query, StringComparison.OrdinalIgnoreCase)) score += 1200;
                else if (host.StartsWith(query, StringComparison.OrdinalIgnoreCase)) score += 700;
            }
            return score;
        }

        private static bool LooksLikeDirectAddressInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input) || input.IndexOf(' ') >= 0) return false;
            if (input.StartsWith("view-source:", StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith("legacyedge-file:", StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (Uri.TryCreate(input, UriKind.Absolute, out Uri absolute) && !string.IsNullOrWhiteSpace(absolute.Scheme)) return true;
            return input.Contains(".");
        }

        private static string OmniboxAddressKey(string address)
        {
            var value = (address ?? string.Empty).Trim();
            if (value.EndsWith("/", StringComparison.Ordinal) && value.IndexOf('?') < 0 && value.IndexOf('#') < 0)
                value = value.TrimEnd('/');
            return value;
        }

        private static string DisplayOmniboxAddress(string address)
        {
            var value = (address ?? string.Empty).Trim();
            if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) value = value.Substring(8);
            else if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) value = value.Substring(7);
            if (value.EndsWith("/", StringComparison.Ordinal) && value.IndexOf('?') < 0 && value.IndexOf('#') < 0) value = value.TrimEnd('/');
            return value;
        }

        private void MoveOmniboxSelection(int direction)
        {
            if (_omniboxSuggestions.Count == 0)
            {
                RefreshOmniboxSuggestions(AddressBox.Text);
                if (_omniboxSuggestions.Count == 0) return;
            }

            var index = OmniboxList.SelectedIndex;
            if (direction > 0) index = index < 0 ? 0 : (index + 1) % _omniboxSuggestions.Count;
            else index = index < 0 ? _omniboxSuggestions.Count - 1 : (index - 1 + _omniboxSuggestions.Count) % _omniboxSuggestions.Count;
            OmniboxList.SelectedIndex = index;
            OmniboxList.ScrollIntoView(_omniboxSuggestions[index]);
            var selected = _omniboxSuggestions[index];
            SetAddressBoxText(selected.Kind == "search" ? _omniboxTypedText : selected.Address);
            AddressBox.Select(AddressBox.Text.Length, 0);
        }

        private void ActivateOmniboxSuggestion(OmniboxSuggestion suggestion)
        {
            if (suggestion == null) return;
            var openInNewTab = (Window.Current.CoreWindow.GetKeyState(VirtualKey.Menu) & CoreVirtualKeyStates.Down) != 0;

            if (suggestion.Kind == "tab" && !openInNewTab)
            {
                var tab = _tabs.FirstOrDefault(candidate => candidate.Id == suggestion.TabId);
                CloseOmniboxPopup(false, true);
                if (tab != null)
                {
                    SwitchTab(tab);
                    FocusCurrentContent();
                    return;
                }
            }

            var address = suggestion.Address;
            CloseOmniboxPopup(false, true);
            if (string.IsNullOrWhiteSpace(address)) return;
            if (openInNewTab)
                CreateTab(address, true, _currentTab?.IsPrivate == true);
            else
                Navigate(_currentTab, address);
            FocusCurrentContent();
        }

        private void DeleteHistorySuggestion(OmniboxSuggestion suggestion)
        {
            if (_history == null || suggestion == null || !suggestion.CanDelete) return;
            var matches = _history.Where(item => string.Equals(OmniboxAddressKey(item.Url), OmniboxAddressKey(suggestion.Address), StringComparison.OrdinalIgnoreCase)).ToList();
            ReplaceItems(_history, BrowserDataStore.RemoveHistoryUrls(matches.Select(item => item.Url)));
            RefreshOmniboxSuggestions(AddressBox.Text);
            ShowTransientStatus("Removed from history.");
        }

        private void PositionOmniboxPopup()
        {
            if (MainToolbar == null || AddressBarHost == null || OmniboxBorder == null || ActualWidth <= 0 || ActualHeight <= 0) return;
            try
            {
                var origin = MainToolbar.TransformToVisual(LayoutRoot).TransformPoint(new Point(0, MainToolbar.ActualHeight));
                var addressOrigin = AddressBarHost.TransformToVisual(LayoutRoot).TransformPoint(new Point(0, 0));
                var reservedBottom = 12d + GetDownloadShelfInset();
                if (LegacySupportPopup?.IsOpen == true) reservedBottom += GetLegacySupportHeight() + 8d;
                var availableHeight = Math.Max(56, ActualHeight - origin.Y - reservedBottom);
                OmniboxBorder.Width = AddressBarHost.ActualWidth;
                OmniboxBorder.Height = double.NaN;
                OmniboxBorder.MaxHeight = availableHeight;
                OmniboxList.MaxHeight = Math.Max(48, availableHeight - 8);
                OmniboxPopup.HorizontalOffset = Math.Max(0, addressOrigin.X);
                OmniboxPopup.VerticalOffset = Math.Max(0, origin.Y);
            }
            catch { }
        }

        private void DismissOmniboxSelectionPreview()
        {
            SetAddressBoxText(_omniboxTypedText);
            OmniboxPopup.IsOpen = false;
            OmniboxList.SelectedIndex = -1;
            _omniboxSuggestions.Clear();
            AddressBox.Select(AddressBox.Text.Length, 0);
        }

        private void CloseOmniboxPopup(bool restoreAddress, bool endEditing)
        {
            OmniboxPopup.IsOpen = false;
            OmniboxList.SelectedIndex = -1;
            _omniboxSuggestions.Clear();
            if (restoreAddress) SetAddressBoxText(_currentTab?.Address ?? _omniboxOriginalText ?? string.Empty);
            if (endEditing) _omniboxEditing = false;
        }

        private void SetAddressBoxText(string value)
        {
            _suppressOmniboxTextChanged = true;
            try { AddressBox.Text = value ?? string.Empty; }
            finally { _suppressOmniboxTextChanged = false; }
        }

        private bool IsInsideOmnibox(DependencyObject element)
        {
            while (element != null)
            {
                if (element == AddressBox || element == OmniboxBorder) return true;
                element = VisualTreeHelper.GetParent(element);
            }
            return false;
        }

        private void FocusCurrentContent()
        {
            if (_currentTab?.IsPdfView == true)
                PdfScrollViewer.Focus(FocusState.Programmatic);
            else
                _currentTab?.View.Focus(FocusState.Programmatic);
        }

        private void SecurityFlyout_Opening(object sender, object e)
        {
            if (_currentTab?.LocalFile != null)
            {
                SecurityFlyoutIcon.Glyph = "\uE8A5";
                SecurityFlyoutIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 95, 95, 95));
                SecurityFlyoutHeading.Text = "Local file";
                SecurityFlyoutHost.Text = _currentTab.LocalFile.Name;
                SecurityFlyoutDetail.Text = "This document is stored on your device and was opened with permission granted through Windows.";
                SecurityFlyoutProtocol.Text = _currentTab.LocalFile.Path;
                return;
            }
            var address = _currentTab?.Address ?? string.Empty;
            Uri.TryCreate(address, UriKind.Absolute, out Uri uri);
            var httpsAddress = uri != null && string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase);
            var secure = httpsAddress && _currentTab?.LastNavigationSucceeded == true;
            var webAddress = uri != null && (string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) || httpsAddress);

            SecurityFlyoutHost.Text = uri != null && !string.IsNullOrWhiteSpace(uri.Host) ? uri.Host : address;
            if (secure)
            {
                SecurityFlyoutIcon.Glyph = "\uE72E";
                SecurityFlyoutIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 120, 85));
                SecurityFlyoutHeading.Text = "Connection is secure";
                SecurityFlyoutDetail.Text = "Your information (for example, passwords or credit card numbers) is private when it is sent to this site.";
                SecurityFlyoutProtocol.Text = "The EdgeHTML security subsystem verified this site's HTTPS connection.";
            }
            else if (httpsAddress)
            {
                SecurityFlyoutIcon.Glyph = "\uE946";
                SecurityFlyoutIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 95, 95, 95));
                SecurityFlyoutHeading.Text = "Connection information unavailable";
                SecurityFlyoutDetail.Text = "This page did not finish loading, so its connection could not be verified.";
                SecurityFlyoutProtocol.Text = "Try reloading the page before entering sensitive information.";
            }
            else if (webAddress)
            {
                SecurityFlyoutIcon.Glyph = "\uE7BA";
                SecurityFlyoutIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 196, 43, 28));
                SecurityFlyoutHeading.Text = "Connection is not secure";
                SecurityFlyoutDetail.Text = "Do not enter sensitive information on this site. It could be seen or changed by someone else.";
                SecurityFlyoutProtocol.Text = "This page was loaded over an unencrypted HTTP connection.";
            }
            else
            {
                SecurityFlyoutIcon.Glyph = "\uE946";
                SecurityFlyoutIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 95, 95, 95));
                SecurityFlyoutHeading.Text = "Site information";
                SecurityFlyoutDetail.Text = "This page has no connection information.";
                SecurityFlyoutProtocol.Text = "Browser and local pages do not use a website connection.";
            }
        }

        private void FavoriteFlyout_Opening(object sender, object e)
        {
            if (_currentTab == null || !IsWebAddress(_currentTab.Address)) return;
            ReplaceItems(_favorites, BrowserDataStore.LoadFavorites());
            ReplaceItems(_readingList, BrowserDataStore.LoadReadingList());
            var existing = _favorites.FirstOrDefault(item => string.Equals(item.Url, _currentTab.Address, StringComparison.OrdinalIgnoreCase));
            var readingItem = _readingList.FirstOrDefault(item => string.Equals(item.Url, _currentTab.Address, StringComparison.OrdinalIgnoreCase));
            var title = existing?.Title ?? _currentTab.Title ?? _currentTab.Address;

            FavoriteFlyoutHeading.Text = existing == null ? "Save this page" : "Favorite saved";
            FavoriteNameBox.Text = title;
            FavoriteNameBox.SelectionStart = title.Length;
            FavoriteLocationBox.SelectedIndex = string.Equals(existing?.Folder, "FavoritesBar", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            FavoriteRemoveButton.Visibility = existing == null ? Visibility.Collapsed : Visibility.Visible;
            FavoritePrimaryButton.Content = existing == null ? "Add" : "Save";
            FavoriteReadingListButton.Content = readingItem == null ? "Add to reading list" : "Remove from reading list";
        }

        private void Favorite_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTab == null || !IsWebAddress(_currentTab.Address)) return;
            FavoriteFlyout.ShowAt(FavoriteButton);
        }

        private void SaveFavorite_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTab == null || !IsWebAddress(_currentTab.Address)) return;
            var existing = _favorites.FirstOrDefault(item => string.Equals(item.Url, _currentTab.Address, StringComparison.OrdinalIgnoreCase));
            var title = string.IsNullOrWhiteSpace(FavoriteNameBox.Text) ? _currentTab.Title : FavoriteNameBox.Text.Trim();
            var location = (FavoriteLocationBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Favorites";
            if (existing == null)
            {
                ReplaceItems(_favorites, BrowserDataStore.UpsertFavorite(new BrowserItem { Title = title, Url = _currentTab.Address, IconUrl = _currentTab.IconUrl, Folder = location }));
                ShowTransientStatus("Added to favorites.");
            }
            else
            {
                existing.Title = title;
                existing.IconUrl = _currentTab.IconUrl;
                existing.Folder = location;
                ReplaceItems(_favorites, BrowserDataStore.UpsertFavorite(existing));
                ShowTransientStatus("Favorite updated.");
            }
            RebuildFavoritesBar();
            UpdateChrome();
            FavoriteFlyout.Hide();
        }

        private void RemoveFavorite_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTab == null) return;
            var existing = _favorites.FirstOrDefault(item => string.Equals(item.Url, _currentTab.Address, StringComparison.OrdinalIgnoreCase));
            if (existing == null) return;
            ReplaceItems(_favorites, BrowserDataStore.RemoveFavorite(existing.Url));
            RebuildFavoritesBar();
            UpdateChrome();
            FavoriteFlyout.Hide();
            ShowTransientStatus("Removed from favorites.");
        }

        private void ToggleReadingList_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTab == null || !IsWebAddress(_currentTab.Address)) return;
            var existing = _readingList.FirstOrDefault(item => string.Equals(item.Url, _currentTab.Address, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                ReplaceItems(_readingList, BrowserDataStore.SetReadingListItem(new BrowserItem { Title = _currentTab.Title, Url = _currentTab.Address, IconUrl = _currentTab.IconUrl }, true));
                ShowTransientStatus("Added to reading list.");
            }
            else
            {
                ReplaceItems(_readingList, BrowserDataStore.SetReadingListItem(existing, false));
                ShowTransientStatus("Removed from reading list.");
            }
            FavoriteFlyout.Hide();
        }

        private void CloseFavoriteFlyout_Click(object sender, RoutedEventArgs e) => FavoriteFlyout.Hide();

        private async void ReadingView_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTab == null) return;
            if (_currentTab.IsPdfView)
            {
                ShowTransientStatus("Reading view is not available for PDF documents.");
                return;
            }
            StopReadAloud();
            if (_currentTab.IsReadingView)
            {
                var original = _currentTab.OriginalAddress;
                _currentTab.IsReadingView = false;
                _currentTab.OriginalAddress = null;
                Navigate(_currentTab, original);
                return;
            }

            if (!IsWebAddress(_currentTab.Address)) return;
            try
            {
                var tab = _currentTab;
                var content = await ExtractReadingContentAsync(tab.View);
                if (tab != _currentTab || content == null || content.Blocks.Count == 0) return;
                _currentTab.OriginalAddress = _currentTab.Address;
                _currentTab.IsReadingView = true;
                _currentTab.Title = string.IsNullOrWhiteSpace(content.Title) ? _currentTab.View.DocumentTitle : content.Title;
                _currentTab.View.NavigateToString(BuildReadingViewHtml(content));
                UpdateChrome();
            }
            catch
            {
                ShowTransientStatus("Reading view is not available for this page.");
            }
        }

        private async void ReadAloud_Click(object sender, RoutedEventArgs e)
        {
            MoreMenu.Hide();
            await StartReadAloudAsync(null);
        }

        private async Task StartReadAloudAsync(string selectedText)
        {
            var tab = _currentTab;
            if (tab?.View == null) return;
            if (tab.IsPdfView)
            {
                ShowTransientStatus("Read aloud isn't available for PDF text in this version.");
                return;
            }
            if (NoteSurface.Visibility == Visibility.Visible) ExitNoteMode();
            StopReadAloud();

            try
            {
                _readAloudSegments.Clear();
                if (!string.IsNullOrWhiteSpace(selectedText))
                {
                    AddSpeechSegments(selectedText, -1, _readAloudSegments);
                }
                else
                {
                    var content = await ExtractReadingContentAsync(tab.View);
                    if (tab != _currentTab || content == null) return;
                    foreach (var block in content.Blocks.Where(block => !string.IsNullOrWhiteSpace(block.Text)))
                        AddSpeechSegments(block.Text, block.SpeechIndex, _readAloudSegments);
                }

                if (_readAloudSegments.Count == 0)
                {
                    ShowTransientStatus("Read aloud is not available for this page.");
                    return;
                }

                EnsureReadAloudSynthesizer();
                _readAloudTab = tab;
                _readAloudIndex = 0;
                _readAloudCompleted = false;
                ReadAloudBar.Visibility = Visibility.Visible;
                await SpeakCurrentSegmentAsync();
            }
            catch
            {
                StopReadAloud();
                ShowTransientStatus("Read aloud could not start. Check that a Windows speech voice is installed.");
            }
        }

        private void EnsureReadAloudSynthesizer()
        {
            if (_readAloudSynthesizer == null) _readAloudSynthesizer = new SpeechSynthesizer();
            _readAloudSynthesizer.Options.SpeakingRate = ReadAloudRateSlider?.Value ?? 1d;
            ReadAloudRateLabel.Text = _readAloudSynthesizer.Options.SpeakingRate.ToString("0.0") + "\u00D7";
            if (_readAloudVoicesReady) return;

            _readAloudVoicesReady = false;
            var voices = SpeechSynthesizer.AllVoices.ToList();
            ReadAloudVoiceBox.ItemsSource = voices;
            var selected = voices.FirstOrDefault(voice => voice.Id == SpeechSynthesizer.DefaultVoice?.Id) ?? voices.FirstOrDefault();
            if (selected != null)
            {
                _readAloudSynthesizer.Voice = selected;
                ReadAloudVoiceBox.SelectedItem = selected;
            }
            _readAloudVoicesReady = true;
        }

        private static void AddSpeechSegments(string text, int elementIndex, IList<SpeechSegment> destination)
        {
            var remaining = NormalizeReadingText(text);
            const int maximumLength = 900;
            while (remaining.Length > 0)
            {
                var length = Math.Min(maximumLength, remaining.Length);
                if (length < remaining.Length)
                {
                    var sentenceBreak = -1;
                    for (var index = length; index >= Math.Min(450, length); index--)
                    {
                        var character = remaining[index - 1];
                        if (character == '.' || character == '!' || character == '?')
                        {
                            sentenceBreak = index;
                            break;
                        }
                    }
                    if (sentenceBreak > 0) length = sentenceBreak;
                    else
                    {
                        var wordBreak = remaining.LastIndexOf(' ', length - 1, length);
                        if (wordBreak > 0) length = wordBreak;
                    }
                }

                var segment = remaining.Substring(0, length).Trim();
                if (segment.Length > 0) destination.Add(new SpeechSegment { Text = segment, ElementIndex = elementIndex });
                remaining = remaining.Substring(length).TrimStart();
            }
        }

        private async Task SpeakCurrentSegmentAsync()
        {
            if (_readAloudIndex < 0 || _readAloudIndex >= _readAloudSegments.Count || _readAloudTab == null)
            {
                CompleteReadAloud();
                return;
            }

            EnsureReadAloudSynthesizer();
            var generation = ++_readAloudGeneration;
            var segment = _readAloudSegments[_readAloudIndex];
            _readAloudCompleted = false;
            _readAloudPlaying = true;
            UpdateReadAloudChrome();
            await HighlightReadAloudElementAsync(_readAloudTab, segment.ElementIndex);

            SpeechSynthesisStream stream = null;
            try
            {
                stream = await _readAloudSynthesizer.SynthesizeTextToStreamAsync(segment.Text);
                if (generation != _readAloudGeneration || ReadAloudBar.Visibility != Visibility.Visible)
                {
                    stream.Dispose();
                    return;
                }

                ReleaseReadAloudStream();
                _readAloudStream = stream;
                ReadAloudPlayer.SetSource(_readAloudStream, _readAloudStream.ContentType);
                ReadAloudPlayer.Play();
            }
            catch
            {
                stream?.Dispose();
                if (generation != _readAloudGeneration) return;
                _readAloudPlaying = false;
                UpdateReadAloudChrome();
                ShowTransientStatus("This section could not be read aloud.");
            }
        }

        private void ReadAloudPrevious_Click(object sender, RoutedEventArgs e)
        {
            if (_readAloudSegments.Count == 0) return;
            _readAloudIndex = Math.Max(0, _readAloudIndex - 1);
            _ = SpeakCurrentSegmentAsync();
        }

        private void ReadAloudNext_Click(object sender, RoutedEventArgs e)
        {
            if (_readAloudSegments.Count == 0) return;
            if (_readAloudIndex >= _readAloudSegments.Count - 1) CompleteReadAloud();
            else
            {
                _readAloudIndex++;
                _ = SpeakCurrentSegmentAsync();
            }
        }

        private void ReadAloudPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_readAloudPlaying)
            {
                ReadAloudPlayer.Pause();
                _readAloudPlaying = false;
                UpdateReadAloudChrome();
                return;
            }

            if (_readAloudCompleted)
            {
                _readAloudIndex = 0;
                _ = SpeakCurrentSegmentAsync();
            }
            else if (_readAloudStream != null)
            {
                ReadAloudPlayer.Play();
                _readAloudPlaying = true;
                UpdateReadAloudChrome();
            }
            else
            {
                _ = SpeakCurrentSegmentAsync();
            }
        }

        private async void ReadAloudPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (ReadAloudBar.Visibility != Visibility.Visible || !_readAloudPlaying) return;
            if (_readAloudIndex >= _readAloudSegments.Count - 1) CompleteReadAloud();
            else
            {
                _readAloudIndex++;
                await SpeakCurrentSegmentAsync();
            }
        }

        private void ReadAloudPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            if (ReadAloudBar.Visibility != Visibility.Visible) return;
            _readAloudPlaying = false;
            UpdateReadAloudChrome();
            ShowTransientStatus("Windows could not play the synthesized speech.");
        }

        private void ReadAloudRateSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (ReadAloudRateLabel != null) ReadAloudRateLabel.Text = e.NewValue.ToString("0.0") + "\u00D7";
            if (_readAloudSynthesizer == null) return;
            _readAloudSynthesizer.Options.SpeakingRate = e.NewValue;
            if (_readAloudPlaying && ReadAloudBar.Visibility == Visibility.Visible) _ = SpeakCurrentSegmentAsync();
        }

        private void ReadAloudVoiceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_readAloudVoicesReady || _readAloudSynthesizer == null || !(ReadAloudVoiceBox.SelectedItem is VoiceInformation voice)) return;
            _readAloudSynthesizer.Voice = voice;
            if (_readAloudPlaying && ReadAloudBar.Visibility == Visibility.Visible) _ = SpeakCurrentSegmentAsync();
        }

        private void CloseReadAloud_Click(object sender, RoutedEventArgs e) => StopReadAloud();

        private void CompleteReadAloud()
        {
            ++_readAloudGeneration;
            ReleaseReadAloudStream();
            _readAloudPlaying = false;
            _readAloudCompleted = true;
            UpdateReadAloudChrome();
            if (_readAloudTab != null) _ = HighlightReadAloudElementAsync(_readAloudTab, -1);
        }

        private void StopReadAloud(bool disposeSynthesizer = false)
        {
            ++_readAloudGeneration;
            var tab = _readAloudTab;
            ReleaseReadAloudStream();
            _readAloudSegments.Clear();
            _readAloudIndex = -1;
            _readAloudPlaying = false;
            _readAloudCompleted = false;
            _readAloudTab = null;
            if (ReadAloudBar != null) ReadAloudBar.Visibility = Visibility.Collapsed;
            if (tab != null) _ = HighlightReadAloudElementAsync(tab, -1);
            UpdateReadAloudChrome();

            if (disposeSynthesizer && _readAloudSynthesizer != null)
            {
                _readAloudSynthesizer.Dispose();
                _readAloudSynthesizer = null;
                _readAloudVoicesReady = false;
            }
        }

        private void ReleaseReadAloudStream()
        {
            if (ReadAloudPlayer != null) ReadAloudPlayer.Stop();
            _readAloudStream?.Dispose();
            _readAloudStream = null;
        }

        private void UpdateReadAloudChrome()
        {
            if (ReadAloudPlayPauseButton == null || ReadAloudPositionText == null) return;
            ReadAloudPlayPauseButton.Content = _readAloudPlaying ? "\uE769" : "\uE768";
            ToolTipService.SetToolTip(ReadAloudPlayPauseButton, _readAloudPlaying ? "Pause" : "Play");
            ReadAloudPositionText.Text = _readAloudCompleted
                ? "Finished"
                : (_readAloudIndex >= 0 && _readAloudSegments.Count > 0 ? (_readAloudIndex + 1) + " of " + _readAloudSegments.Count : string.Empty);
        }

        private static async Task HighlightReadAloudElementAsync(BrowserTab tab, int elementIndex)
        {
            if (tab?.View == null) return;
            try
            {
                var script = "(function(){var old=document.querySelectorAll('.legacy-edge-speaking');for(var i=0;i<old.length;i++)old[i].classList.remove('legacy-edge-speaking');";
                if (elementIndex >= 0)
                    script += "var e=document.querySelector('[data-legacy-edge-speech-index=\\\"" + elementIndex + "\\\"]');if(e){e.classList.add('legacy-edge-speaking');e.scrollIntoView(false);}";
                script += "return 'ok';})()";
                await tab.View.InvokeScriptAsync("eval", new[] { script });
            }
            catch { }
        }

        private void Hub_Click(object sender, RoutedEventArgs e)
        {
            if (SidePanePopup.IsOpen && HubPanel.Visibility == Visibility.Visible)
                SidePanePopup.IsOpen = false;
            else
                OpenHub(_activeHub);
        }
        private void Favorites_Click(object sender, RoutedEventArgs e) { MoreMenu.Hide(); OpenHub("favorites"); }
        private void ReadingList_Click(object sender, RoutedEventArgs e) => OpenHub("reading");
        private void Books_Click(object sender, RoutedEventArgs e) => OpenHub("books");
        private void History_Click(object sender, RoutedEventArgs e) { MoreMenu.Hide(); OpenHub("history"); }
        private void Downloads_Click(object sender, RoutedEventArgs e) { MoreMenu.Hide(); OpenHub("downloads"); }
        private void ShowSetAsideTabs_Click(object sender, RoutedEventArgs e)
        {
            if (_windowIsPrivate)
            {
                ShowTransientStatus("Tabs set aside isn't available in an InPrivate window.");
                return;
            }
            OpenHub("setaside");
        }

        private void SetTabsAside_Click(object sender, RoutedEventArgs e)
        {
            if (_windowIsPrivate)
            {
                ShowTransientStatus("Tabs set aside isn't available in an InPrivate window.");
                return;
            }
            if (_tabs.Count == 0) return;
            if (_capturingWebView != null)
            {
                ShowTransientStatus("Finish the current page capture, then set the tabs aside.");
                return;
            }

            var setAsideBatch = _tabs.Where(item => !item.IsPrivate)
                .Select(tab => new BrowserItem
                {
                    Title = string.IsNullOrWhiteSpace(tab.Title) ? "New tab" : tab.Title,
                    Url = !string.IsNullOrWhiteSpace(tab.LocalFileToken) ? "legacyedge-file:" + tab.LocalFileToken : (string.IsNullOrWhiteSpace(tab.Address) ? "about:blank" : tab.Address),
                    IconUrl = tab.IconUrl
                }).ToList();
            ReplaceItems(_setAsideTabs, BrowserDataStore.AddSetAsideTabs(setAsideBatch));

            ++_tabPreviewGeneration;
            ++_findGeneration;
            StopReadAloud();
            _contextTab = null;
            foreach (var tab in _tabs.ToList()) RetireWebView(tab);

            _tabs.Clear();
            ++_tabStructureVersion;
            _currentTab = null;
            CreateTab();
            ShowTransientStatus("Tabs set aside.");
        }

        private void ShowTabs_Click(object sender, RoutedEventArgs e)
        {
            if (TabPreviewBar.Visibility == Visibility.Visible)
            {
                HideTabPreviews();
                return;
            }

            TabPreviewBar.Visibility = Visibility.Visible;
            TabPreviewToggleButton.Content = "\uE70E";
            ToolTipService.SetToolTip(TabPreviewToggleButton, "Hide tab previews");
            RebuildTabPreviewCards(true);
            PositionSidePane();
        }

        private void HideTabPreviews()
        {
            ++_tabPreviewGeneration;
            if (TabPreviewBar != null) TabPreviewBar.Visibility = Visibility.Collapsed;
            if (TabPreviewToggleButton != null)
            {
                TabPreviewToggleButton.Content = "\uE70D";
                ToolTipService.SetToolTip(TabPreviewToggleButton, "Show tab previews");
            }
            PositionSidePane();
        }

        private void RebuildTabPreviewCards(bool captureFreshImages)
        {
            if (TabPreviewPanel == null || TabPreviewBar.Visibility != Visibility.Visible) return;
            var generation = ++_tabPreviewGeneration;
            TabPreviewPanel.Children.Clear();
            TabPreviewEmptyText.Visibility = _tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var targets = new List<TabPreviewCaptureTarget>();
            var cardBackground = IsDarkTheme ? Color.FromArgb(255, 23, 23, 23) : Color.FromArgb(255, 248, 248, 248);
            var previewBackground = IsDarkTheme ? Color.FromArgb(255, 31, 31, 31) : Colors.White;
            var fallbackBackground = IsDarkTheme ? Color.FromArgb(255, 31, 31, 31) : Color.FromArgb(255, 242, 242, 242);
            var fallbackForeground = IsDarkTheme ? Color.FromArgb(255, 175, 175, 175) : Color.FromArgb(255, 120, 120, 120);
            var footerBackground = IsDarkTheme ? Color.FromArgb(255, 23, 23, 23) : Color.FromArgb(255, 244, 244, 244);
            var selectedFooterBackground = IsDarkTheme ? Color.FromArgb(255, 36, 55, 70) : Color.FromArgb(255, 238, 246, 252);
            var previewText = IsDarkTheme ? Colors.White : Color.FromArgb(255, 32, 32, 32);
            var unselectedBorder = IsDarkTheme ? Color.FromArgb(255, 91, 91, 91) : Color.FromArgb(255, 148, 148, 148);

            foreach (var tab in _tabs)
            {
                var card = new Border
                {
                    Width = tab.IsPinned ? 150 : 220,
                    Height = 120,
                    Margin = new Thickness(0, 0, 8, 0),
                    Background = new SolidColorBrush(cardBackground),
                    BorderBrush = new SolidColorBrush(tab == _currentTab ? Color.FromArgb(255, 0, 120, 215) : unselectedBorder),
                    BorderThickness = new Thickness(tab == _currentTab ? 2 : 1),
                    Tag = tab
                };
                card.ContextFlyout = BuildTabContextMenu(tab);

                var layout = new Grid();
                layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(88) });
                layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                var previewHost = new Grid
                {
                    Background = new SolidColorBrush(previewBackground),
                    IsHitTestVisible = false
                };
                var fallback = new Grid { Background = new SolidColorBrush(fallbackBackground) };
                fallback.Children.Add(new FontIcon
                {
                    Glyph = tab.IsPdfView ? "\uEA90" : "\uE774",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 28,
                    Foreground = new SolidColorBrush(fallbackForeground),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
                var image = new Image
                {
                    Stretch = Stretch.UniformToFill,
                    Visibility = tab.PreviewImage == null ? Visibility.Collapsed : Visibility.Visible,
                    Source = tab.PreviewImage
                };
                fallback.Visibility = tab.PreviewImage == null ? Visibility.Visible : Visibility.Collapsed;
                previewHost.Children.Add(fallback);
                previewHost.Children.Add(image);
                Grid.SetRow(previewHost, 0);

                var footer = new Grid
                {
                    Background = new SolidColorBrush(tab == _currentTab ? selectedFooterBackground : footerBackground),
                    Padding = new Thickness(8, 0, 30, 0)
                };
                footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                footer.Children.Add(CreateTabFavicon(tab, 16, HorizontalAlignment.Left));
                var title = new TextBlock
                {
                    Text = (tab.IsPinned ? "Pinned  " : string.Empty) + (string.IsNullOrWhiteSpace(tab.Title) ? "New tab" : tab.Title),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12,
                    Foreground = new SolidColorBrush(previewText),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(title, 1);
                footer.Children.Add(title);
                Grid.SetRow(footer, 1);

                var close = new Button
                {
                    Content = "\uE711",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 10,
                    Width = 30,
                    Height = 30,
                    Padding = new Thickness(0),
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Tag = tab
                };
                close.Click += (closeSender, closeArgs) => CloseTab((BrowserTab)((FrameworkElement)closeSender).Tag);
                Grid.SetRow(close, 1);

                layout.Children.Add(previewHost);
                layout.Children.Add(footer);
                layout.Children.Add(close);
                card.Child = layout;
                card.Tapped += (cardSender, cardArgs) => SwitchTab((BrowserTab)((FrameworkElement)cardSender).Tag);
                ToolTipService.SetToolTip(card, string.IsNullOrWhiteSpace(tab.Address) ? tab.Title : tab.Address);
                TabPreviewPanel.Children.Add(card);

                if (captureFreshImages)
                    targets.Add(new TabPreviewCaptureTarget { Tab = tab, Image = image, Fallback = fallback });
            }

            if (targets.Count > 0) _ = RefreshTabPreviewImagesAsync(targets, generation);
        }

        private Grid CreateTabFavicon(BrowserTab tab, double size, HorizontalAlignment alignment)
        {
            var host = new Grid
            {
                Width = size,
                Height = size,
                HorizontalAlignment = alignment,
                VerticalAlignment = VerticalAlignment.Center
            };
            var fallback = new TextBlock
            {
                Text = tab.IsPdfView ? "\uEA90" : "\uE774",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = Math.Max(11, size - 2),
                Foreground = new SolidColorBrush(IsDarkTheme ? Color.FromArgb(255, 196, 196, 196) : Color.FromArgb(255, 70, 70, 70)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            host.Children.Add(fallback);
            if (Uri.TryCreate(tab.IconUrl, UriKind.Absolute, out Uri iconUri) &&
                (iconUri.Scheme == Uri.UriSchemeHttp || iconUri.Scheme == Uri.UriSchemeHttps))
            {
                var favicon = new Image { Width = size, Height = size, Stretch = Stretch.Uniform, Source = new BitmapImage(iconUri) };
                favicon.ImageOpened += (sender, args) => fallback.Visibility = Visibility.Collapsed;
                favicon.ImageFailed += (sender, args) => favicon.Visibility = Visibility.Collapsed;
                host.Children.Add(favicon);
            }
            return host;
        }

        private async Task RefreshTabPreviewImagesAsync(IEnumerable<TabPreviewCaptureTarget> targets, int generation)
        {
            foreach (var target in targets.OrderBy(target => target.Tab == _currentTab ? 0 : 1))
            {
                if (generation != _tabPreviewGeneration || TabPreviewBar.Visibility != Visibility.Visible) return;
                await CaptureTabPreviewAsync(target.Tab, target.Image, target.Fallback, generation);
            }
        }

        private async Task CaptureTabPreviewAsync(BrowserTab tab, Image target, FrameworkElement fallback, int generation)
        {
            var view = tab?.View;
            if (view == null || tab.IsLoading) return;
            var viewGeneration = tab.ViewGeneration;
            try
            {
                if (tab.IsPdfView && tab == _currentTab && PdfSurface.Visibility == Visibility.Visible)
                {
                    var pdfBitmap = await CaptureElementImageAsync(PdfSurface);
                    if (pdfBitmap == null || !IsLiveWebView(tab, view, viewGeneration) || tab != _currentTab) return;
                    tab.PreviewImage = pdfBitmap;
                    if (generation >= 0 && (generation != _tabPreviewGeneration || TabPreviewBar.Visibility != Visibility.Visible)) return;
                    if (target != null)
                    {
                        target.Source = pdfBitmap;
                        target.Visibility = Visibility.Visible;
                    }
                    if (fallback != null) fallback.Visibility = Visibility.Collapsed;
                    return;
                }

                // Detached background WebViews are deliberately dormant. Use
                // their cached preview/placeholder rather than invoking the
                // EdgeHTML capture pipeline on an unregistered component host.
                if (tab != _currentTab || view.Visibility != Visibility.Visible || !BrowserHost.Children.Contains(view)) return;
                if (!TryBeginWebViewCapture(view)) return;

                try
                {
                    using (var stream = new InMemoryRandomAccessStream())
                    {
                        await view.CapturePreviewToStreamAsync(stream);
                        if (!IsLiveWebView(tab, view, viewGeneration) || tab != _currentTab || !BrowserHost.Children.Contains(view)) return;
                        stream.Seek(0);
                        var bitmap = new BitmapImage();
                        await bitmap.SetSourceAsync(stream);
                        if (!IsLiveWebView(tab, view, viewGeneration) || tab != _currentTab || !BrowserHost.Children.Contains(view)) return;
                        tab.PreviewImage = bitmap;
                        if (generation >= 0 && (generation != _tabPreviewGeneration || TabPreviewBar.Visibility != Visibility.Visible)) return;
                        if (target != null)
                        {
                            target.Source = bitmap;
                            target.Visibility = Visibility.Visible;
                        }
                        if (fallback != null) fallback.Visibility = Visibility.Collapsed;
                    }
                }
                finally { EndWebViewCapture(view); }
            }
            catch
            {
                // EdgeHTML cannot capture some protected or background surfaces; retain the last thumbnail or placeholder.
            }
        }

        private static async Task<BitmapImage> CaptureElementImageAsync(UIElement element)
        {
            if (element == null) return null;
            var rendered = new RenderTargetBitmap();
            await rendered.RenderAsync(element);
            if (rendered.PixelWidth == 0 || rendered.PixelHeight == 0) return null;
            var buffer = await rendered.GetPixelsAsync();
            var pixels = new byte[(int)buffer.Length];
            using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
            using (var stream = new InMemoryRandomAccessStream())
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)rendered.PixelWidth, (uint)rendered.PixelHeight, 96, 96, pixels);
                await encoder.FlushAsync();
                stream.Seek(0);
                var image = new BitmapImage();
                await image.SetSourceAsync(stream);
                return image;
            }
        }

        private MenuFlyout BuildTabContextMenu(BrowserTab tab)
        {
            var menu = new MenuFlyout { MenuFlyoutPresenterStyle = Resources["PageContextMenuPresenterStyle"] as Style };
            menu.Items.Add(CreateTabContextItem("Reload tab", (sender, args) => ReloadTab(tab), _tabs.Contains(tab)));
            menu.Items.Add(CreateTabContextItem("Duplicate tab", (sender, args) => DuplicateTab(tab), _tabs.Contains(tab)));
            menu.Items.Add(CreateTabContextItem(tab.IsPinned ? "Unpin tab" : "Pin tab", (sender, args) => TogglePinnedTab(tab), _tabs.Contains(tab)));
            menu.Items.Add(CreateTabContextItem("Move tab to new window", async (sender, args) => await MoveTabToNewWindowAsync(tab), _tabs.Count > 1));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(CreateTabContextItem("Close tab", (sender, args) => CloseTab(tab), _tabs.Contains(tab)));
            menu.Items.Add(CreateTabContextItem("Close other tabs", (sender, args) => CloseOtherTabs(tab), _tabs.Count > 1));
            var index = _tabs.IndexOf(tab);
            menu.Items.Add(CreateTabContextItem("Close tabs to the right", (sender, args) => CloseTabsToRight(tab), index >= 0 && index < _tabs.Count - 1));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(CreateTabContextItem("Reopen closed tab", (sender, args) => ReopenClosedTab(), _closedTabs.Count > 0));
            return menu;
        }

        private MenuFlyoutItem CreateTabContextItem(string text, RoutedEventHandler handler, bool enabled)
        {
            var item = new MenuFlyoutItem
            {
                Text = text,
                IsEnabled = enabled,
                Style = Resources["PageContextMenuItemStyle"] as Style
            };
            item.Click += handler;
            return item;
        }

        private void DuplicateTab(BrowserTab tab)
        {
            if (tab == null || !_tabs.Contains(tab)) return;
            if (!string.IsNullOrWhiteSpace(tab.LocalFileToken))
            {
                var localDuplicate = CreateTab(null, true, tab.IsPrivate, false);
                _ = OpenStoredFileAsync(localDuplicate, tab.LocalFileToken);
                return;
            }
            var address = tab.IsReadingView ? tab.OriginalAddress : tab.Address;
            var duplicate = CreateTab(string.IsNullOrWhiteSpace(address) ? null : address, true, tab.IsPrivate);
            duplicate.ZoomPercent = tab.ZoomPercent;
            _ = ApplyZoomAsync(duplicate);
        }

        private void ReloadTab(BrowserTab tab)
        {
            if (tab == null || !_tabs.Contains(tab)) return;
            if (tab.IsPdfView && tab.LocalFile != null) _ = OpenLocalPdfAsync(tab, tab.LocalFile);
            else if (tab.IsPdfView && Uri.TryCreate(tab.PdfSourceAddress, UriKind.Absolute, out Uri uri)) _ = OpenPdfAsync(tab, uri);
            else if (tab.LocalFile != null) _ = OpenLocalFileAsync(tab, tab.LocalFile, tab.LocalFileToken);
            else tab.View?.Refresh();
        }

        private void TogglePinnedTab(BrowserTab tab)
        {
            if (tab == null || !_tabs.Contains(tab)) return;
            _tabs.Remove(tab);
            tab.IsPinned = !tab.IsPinned;
            var insertionIndex = _tabs.TakeWhile(candidate => candidate.IsPinned).Count();
            _tabs.Insert(insertionIndex, tab);
            ++_tabStructureVersion;
            RebuildTabs();
            UpdateSessionSnapshot();
        }

        private async Task MoveTabToNewWindowAsync(BrowserTab tab)
        {
            if (tab == null || !_tabs.Contains(tab)) return;
            var address = tab.IsReadingView ? tab.OriginalAddress : tab.Address;
            if (await OpenNewWindowAsync(address, tab.IsPrivate, tab.LocalFile)) CloseTab(tab, false);
            else ShowTransientStatus("The tab could not be moved to a new window.");
        }

        private void CloseOtherTabs(BrowserTab tab)
        {
            if (tab == null || !_tabs.Contains(tab)) return;
            foreach (var candidate in _tabs.Where(candidate => candidate != tab).ToList()) CloseTab(candidate);
            SwitchTab(tab);
        }

        private void CloseTabsToRight(BrowserTab tab)
        {
            var index = _tabs.IndexOf(tab);
            if (index < 0) return;
            foreach (var candidate in _tabs.Skip(index + 1).ToList()) CloseTab(candidate);
            SwitchTab(tab);
        }

        private void ReopenClosedTab()
        {
            if (_closedTabs.Count > 0) CreateTab(_closedTabs.Pop());
        }

        private void OpenHub(string section)
        {
            CloseOmniboxPopup(false, true);
            _activeHub = section;
            HubPanel.Visibility = Visibility.Visible;
            SettingsPanel.Visibility = Visibility.Collapsed;
            SetHubExpanded(_hubExpanded);
            if (section == "favorites")
            {
                ReplaceItems(_favorites, BrowserDataStore.LoadFavorites());
                RebuildFavoritesBar();
            }
            else if (section == "reading") ReplaceItems(_readingList, BrowserDataStore.LoadReadingList());
            else if (section == "history") ReplaceItems(_history, BrowserDataStore.LoadHistory());
            else if (section == "setaside") ReplaceItems(_setAsideTabs, BrowserDataStore.LoadSetAsideTabs());
            switch (section)
            {
                case "reading": HubHeading.Text = "Reading list"; HubList.ItemsSource = _readingList; break;
                case "books": HubHeading.Text = "Books"; HubList.ItemsSource = _books; break;
                case "history": HubHeading.Text = "History"; HubList.ItemsSource = _history; break;
                case "downloads": HubHeading.Text = "Downloads"; break;
                case "setaside": HubHeading.Text = "Tabs you've set aside"; HubList.ItemsSource = _setAsideTabs; break;
                case "tabs": HubHeading.Text = "Open tabs"; HubList.ItemsSource = _openTabsSnapshot; break;
                default: HubHeading.Text = "Favorites"; HubList.ItemsSource = _favorites; break;
            }
            UpdateHubPresentation();
            PositionSidePane();
            SidePanePopup.IsOpen = true;
            ApplyHubPinLayout();
        }

        private void UpdateHubPresentation()
        {
            HubFavoritesIndicator.Visibility = _activeHub == "favorites" ? Visibility.Visible : Visibility.Collapsed;
            HubReadingIndicator.Visibility = _activeHub == "reading" ? Visibility.Visible : Visibility.Collapsed;
            HubBooksIndicator.Visibility = _activeHub == "books" ? Visibility.Visible : Visibility.Collapsed;
            HubHistoryIndicator.Visibility = _activeHub == "history" ? Visibility.Visible : Visibility.Collapsed;
            HubDownloadsIndicator.Visibility = _activeHub == "downloads" ? Visibility.Visible : Visibility.Collapsed;
            HubNavFavoritesIndicator.Visibility = _activeHub == "favorites" ? Visibility.Visible : Visibility.Collapsed;
            HubNavReadingIndicator.Visibility = _activeHub == "reading" ? Visibility.Visible : Visibility.Collapsed;
            HubNavBooksIndicator.Visibility = _activeHub == "books" ? Visibility.Visible : Visibility.Collapsed;
            HubNavHistoryIndicator.Visibility = _activeHub == "history" ? Visibility.Visible : Visibility.Collapsed;
            HubNavDownloadsIndicator.Visibility = _activeHub == "downloads" ? Visibility.Visible : Visibility.Collapsed;

            var downloadsActive = _activeHub == "downloads";
            var itemCount = downloadsActive ? (_downloads?.Count ?? 0) : HubList.Items.Count;
            var isEmpty = itemCount == 0;
            HubList.Visibility = !downloadsActive && !isEmpty ? Visibility.Visible : Visibility.Collapsed;
            DownloadsList.Visibility = downloadsActive && !isEmpty ? Visibility.Visible : Visibility.Collapsed;
            ActiveDownloadsList.Visibility = downloadsActive && _activeDownloads.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            PastDownloadsList.Visibility = downloadsActive && _pastDownloads.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            PastDownloadsHeader.Visibility = downloadsActive && _pastDownloads.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            HubEmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            HubClearButton.Visibility = !isEmpty && (_activeHub == "history" || _activeHub == "setaside") ? Visibility.Visible : Visibility.Collapsed;
            HubOpenDownloadsButton.Visibility = downloadsActive ? Visibility.Visible : Visibility.Collapsed;
            HubDownloadHeaderDivider.Visibility = downloadsActive ? Visibility.Visible : Visibility.Collapsed;

            switch (_activeHub)
            {
                case "reading":
                    HubEmptyGlyph.Glyph = "";
                    HubEmptyTitle.Text = "Your reading list is empty";
                    HubEmptyDetail.Text = "Save articles from the star menu to read them later.";
                    break;
                case "history":
                    HubEmptyGlyph.Glyph = "";
                    HubEmptyTitle.Text = "No browsing history";
                    HubEmptyDetail.Text = "Pages you visit outside InPrivate tabs appear here.";
                    break;
                case "books":
                    HubEmptyGlyph.Glyph = "\uE82D";
                    HubEmptyTitle.Text = "No books yet";
                    HubEmptyDetail.Text = "Open an EPUB book to add it to your library.";
                    break;
                case "downloads":
                    HubEmptyGlyph.Glyph = "";
                    HubEmptyTitle.Text = "No downloads";
                    HubEmptyDetail.Text = "Files downloaded with Microsoft Edge appear here.";
                    break;
                case "setaside":
                    HubEmptyGlyph.Glyph = "";
                    HubEmptyTitle.Text = "No tabs set aside";
                    HubEmptyDetail.Text = "Use the set-aside button on the tab strip to save a group of tabs.";
                    break;
                case "tabs":
                    HubEmptyGlyph.Glyph = "";
                    HubEmptyTitle.Text = "No open tabs";
                    HubEmptyDetail.Text = "Create a new tab to start browsing.";
                    break;
                default:
                    HubEmptyGlyph.Glyph = "";
                    HubEmptyTitle.Text = "No favorites yet";
                    HubEmptyDetail.Text = "Select the star in the address bar to save this page.";
                    break;
            }
        }

        private void ApplyDownloadSnapshots(IEnumerable<DownloadItem> snapshots, bool notifyShelf)
        {
            var ordered = (snapshots ?? Enumerable.Empty<DownloadItem>())
                .OrderByDescending(item => item.Added)
                .ToList();

            if (_downloads == null) _downloads = new ObservableCollection<DownloadItem>();
            _downloads.Clear();
            foreach (var snapshot in ordered) _downloads.Add(snapshot.CloneSnapshot());

            // Preserve the bound instances and ListView containers while broker
            // progress arrives. Clearing and rebuilding these collections for
            // every progress report leaves the legacy Hub visibly blank between
            // layout passes on high-DPI systems.
            MergeDownloadCollection(_activeDownloads, ordered.Where(item => item.IsActive));
            MergeDownloadCollection(_pastDownloads, ordered.Where(item => !item.IsActive));

            if (!string.IsNullOrWhiteSpace(_openAfterDownloadId))
            {
                var openItem = ordered.FirstOrDefault(item => string.Equals(item.Id, _openAfterDownloadId, StringComparison.Ordinal));
                if (openItem?.State == LegacyDownloadState.Completed)
                {
                    _openAfterDownloadId = null;
                    _ = OpenDownloadedFileAsync(openItem);
                }
                else if (openItem != null && openItem.IsTerminal)
                {
                    _openAfterDownloadId = null;
                }
            }

            UpdateDownloadShelf(ordered.FirstOrDefault(), notifyShelf);
            if (_activeHub == "downloads" && HubPanel.Visibility == Visibility.Visible)
                UpdateHubPresentation();
        }

        private static void MergeDownloadCollection(ObservableCollection<DownloadItem> target, IEnumerable<DownloadItem> snapshots)
        {
            var desired = (snapshots ?? Enumerable.Empty<DownloadItem>()).ToList();
            var desiredIds = new HashSet<string>(desired.Select(item => item.Id), StringComparer.Ordinal);

            for (var index = target.Count - 1; index >= 0; index--)
            {
                if (!desiredIds.Contains(target[index].Id)) target.RemoveAt(index);
            }

            for (var index = 0; index < desired.Count; index++)
            {
                var snapshot = desired[index];
                var existingIndex = -1;
                for (var candidate = index; candidate < target.Count; candidate++)
                {
                    if (string.Equals(target[candidate].Id, snapshot.Id, StringComparison.Ordinal))
                    {
                        existingIndex = candidate;
                        break;
                    }
                }

                if (existingIndex < 0)
                {
                    target.Insert(index, snapshot.CloneSnapshot());
                    continue;
                }

                if (existingIndex != index) target.Move(existingIndex, index);
                target[index].CopyPresentationFrom(snapshot);
            }
        }

        private void UpdateDownloadShelf(DownloadItem newest, bool notify)
        {
            if (_pendingDownloadUri != null) return;
            ConfigureDownloadShelfActionButtons(false);
            if (newest == null)
            {
                _downloadShelfItemId = null;
                _downloadShelfLastState = null;
                DownloadShelf.Visibility = Visibility.Collapsed;
                UpdateBottomNotificationPositions();
                return;
            }

            var itemChanged = !string.Equals(_downloadShelfItemId, newest.Id, StringComparison.Ordinal);
            var stateChanged = _downloadShelfLastState != newest.State;
            if (notify && (itemChanged || stateChanged)) _downloadShelfHidden = false;
            if (!notify) _downloadShelfHidden = true;
            _downloadShelfItemId = newest.Id;
            _downloadShelfLastState = newest.State;

            if (newest.State == LegacyDownloadState.Completed)
            {
                DownloadShelfTitleText.Text = newest.Title + " finished downloading.";
                DownloadShelfDetailText.Text = "From: " + newest.SourceText;
            }
            else if (newest.State == LegacyDownloadState.Failed || newest.State == LegacyDownloadState.Canceled || newest.State == LegacyDownloadState.Missing)
            {
                DownloadShelfTitleText.Text = newest.Title + " couldn't be downloaded.";
                DownloadShelfDetailText.Text = newest.StatusText;
            }
            else
            {
                DownloadShelfTitleText.Text = newest.Title;
                DownloadShelfDetailText.Text = newest.StatusText + (string.IsNullOrWhiteSpace(newest.SourceText) ? string.Empty : "  From: " + newest.SourceText);
            }

            DownloadShelfPauseButton.Content = newest.CanResume ? "Resume" : "Pause";
            DownloadShelfPauseButton.Visibility = newest.CanPause || newest.CanResume ? Visibility.Visible : Visibility.Collapsed;
            DownloadShelfCancelButton.Visibility = newest.CanCancel ? Visibility.Visible : Visibility.Collapsed;
            DownloadShelfOpenButton.Content = newest.CanRetry ? "Retry" : "Open";
            DownloadShelfOpenButton.Visibility = newest.CanOpen || newest.CanRetry ? Visibility.Visible : Visibility.Collapsed;
            DownloadShelfFolderButton.Visibility = newest.CanOpen ? Visibility.Visible : Visibility.Collapsed;
            DownloadShelfViewButton.Visibility = Visibility.Visible;
            DownloadShelfProgress.Visibility = newest.IsActive ? Visibility.Visible : Visibility.Collapsed;
            DownloadShelfProgress.IsIndeterminate = newest.IsIndeterminate;
            DownloadShelfProgress.Value = newest.ProgressValue;
            DownloadShelf.Visibility = _downloadShelfHidden ? Visibility.Collapsed : Visibility.Visible;
            UpdateBottomNotificationPositions();
        }

        private void ConfigureDownloadShelfActionButtons(bool prompt)
        {
            DownloadShelfOpenButton.Margin = prompt ? new Thickness(4, 0, 0, 0) : new Thickness(4, 0, 4, 0);
            DownloadShelfFolderButton.Margin = prompt ? new Thickness(0, 0, 4, 0) : new Thickness(4, 0, 4, 0);
            DownloadShelfFolderButton.MinWidth = prompt ? 34 : 98;
            DownloadShelfFolderButton.Width = prompt ? 34 : double.NaN;
            DownloadShelfFolderButton.FontFamily = new FontFamily(prompt ? "Segoe MDL2 Assets" : "Segoe UI");
            DownloadShelfFolderButton.FontSize = prompt ? 12 : 14;
            DownloadShelfFolderButton.Content = prompt ? "\uE70D" : "Open folder";
            ToolTipService.SetToolTip(DownloadShelfFolderButton, prompt ? "Save as" : "Open folder");
        }

        private void UpdateDownloadShelfLayout(double width)
        {
            if (DownloadShelfLayout == null || DownloadShelfActionsRow == null) return;
            var shelfWidth = Math.Max(320, Math.Min(960, width));
            var compact = shelfWidth < 760;

            DownloadShelf.Width = shelfWidth;
            DownloadShelf.HorizontalAlignment = HorizontalAlignment.Center;

            DownloadShelf.MinHeight = compact ? 120 : 72;
            DownloadShelfActionsRow.Height = compact ? GridLength.Auto : new GridLength(0);
            DownloadShelfTextHost.Margin = compact ? new Thickness(16, 9, 8, 5) : new Thickness(18, 10, 16, 10);
            DownloadShelfActionHost.Margin = compact ? new Thickness(8, 0, 8, 10) : new Thickness(0);
            DownloadShelfActionHost.HorizontalAlignment = HorizontalAlignment.Right;

            Grid.SetRow(DownloadShelfTextHost, 0);
            Grid.SetColumn(DownloadShelfTextHost, 0);
            Grid.SetColumnSpan(DownloadShelfTextHost, compact ? 2 : 1);
            Grid.SetRow(DownloadShelfActionHost, compact ? 1 : 0);
            Grid.SetColumn(DownloadShelfActionHost, compact ? 0 : 1);
            Grid.SetColumnSpan(DownloadShelfActionHost, compact ? 3 : 1);
            Grid.SetRow(DownloadShelfCloseButton, 0);
            Grid.SetColumn(DownloadShelfCloseButton, 2);
            Grid.SetRowSpan(DownloadShelfProgress, compact ? 2 : 1);

            DownloadShelfCloseButton.VerticalAlignment = compact ? VerticalAlignment.Top : VerticalAlignment.Center;
            DownloadShelfCloseButton.Margin = compact ? new Thickness(0, 4, 0, 0) : new Thickness(0);
            DownloadShelfTitleText.TextWrapping = compact ? TextWrapping.Wrap : TextWrapping.NoWrap;
            DownloadShelfDetailText.TextWrapping = compact ? TextWrapping.Wrap : TextWrapping.NoWrap;
            DownloadShelfTitleText.MaxLines = compact ? 2 : 1;
            DownloadShelfDetailText.MaxLines = compact ? 2 : 1;
        }

        private void UpdateLegacySupportLayout(double width)
        {
            if (LegacySupportLayout == null || LegacySupportActionsRow == null) return;
            var compact = width < 720;

            LegacySupportBorder.Height = double.NaN;
            LegacySupportBorder.MinHeight = compact ? 124 : 80;
            LegacySupportBorder.Padding = compact ? new Thickness(18, 8, 8, 8) : new Thickness(46, 0, 14, 0);
            LegacySupportActionsRow.Height = compact ? GridLength.Auto : new GridLength(0);
            LegacySupportDownloadColumn.Width = new GridLength(compact ? 130 : 150);
            LegacySupportNotNowColumn.Width = new GridLength(compact ? 120 : 150);
            LegacySupportCloseColumn.Width = new GridLength(compact ? 44 : 50);

            Grid.SetRow(LegacySupportText, 0);
            Grid.SetColumn(LegacySupportText, 0);
            Grid.SetColumnSpan(LegacySupportText, compact ? 4 : 1);
            LegacySupportText.FontSize = compact ? 14 : 15;
            LegacySupportText.Margin = compact ? new Thickness(0, 0, 0, 5) : new Thickness(0, 0, 18, 0);
            LegacySupportText.MaxLines = 0;

            foreach (var button in new[] { LegacySupportDownloadButton, LegacySupportNotNowButton, LegacySupportCloseButton })
                Grid.SetRow(button, compact ? 1 : 0);
            LegacySupportDownloadButton.Height = double.NaN;
            LegacySupportNotNowButton.Height = double.NaN;
            LegacySupportDownloadButton.MinHeight = compact ? 38 : 42;
            LegacySupportNotNowButton.MinHeight = compact ? 38 : 42;
            LegacySupportCloseButton.Height = compact ? 40 : 44;
            LegacySupportDownloadButton.FontSize = compact ? 14 : 15;
            LegacySupportNotNowButton.FontSize = compact ? 14 : 15;
        }

        private void HubNavigationToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!_hubExpanded && _hubPinned)
            {
                var width = ActualWidth > 0 ? ActualWidth : Window.Current.Bounds.Width;
                if (width < 562 + 560)
                {
                    ShowTransientStatus("Make the window wider to expand the pinned Hub.");
                    return;
                }
            }
            _hubExpanded = !_hubExpanded;
            SetHubExpanded(_hubExpanded);
            PositionSidePane();
        }

        private void SetHubExpanded(bool expanded)
        {
            if (HubNavigationColumn == null) return;
            _hubExpanded = expanded;
            ApplyHubExpansionVisual(IsHubEffectivelyExpanded());
            UpdateSidePaneWidth();
        }

        private bool IsHubEffectivelyExpanded()
        {
            var width = ActualWidth > 0 ? ActualWidth : Window.Current.Bounds.Width;
            return _hubExpanded && width >= 720;
        }

        private void ApplyHubExpansionVisual(bool expanded)
        {
            HubNavigationColumn.Width = new GridLength(expanded ? 200 : 0);
            HubNavigationRail.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            HubCompactTabs.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            HubExpandButton.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            HubCompactTabsRow.Height = new GridLength(expanded ? 0 : 52);
            HubHeaderRow.Height = new GridLength(expanded ? 65 : 56);
        }

        private void HubPin_Click(object sender, RoutedEventArgs e)
        {
            if (!_hubPinned && !CanDockHub())
            {
                ShowTransientStatus("Make the window wider to pin the Hub beside the page.");
                return;
            }
            SetHubPinned(!_hubPinned);
        }

        private void SetHubPinned(bool pinned)
        {
            _hubPinned = pinned;
            SidePanePopup.IsLightDismissEnabled = !_hubPinned;
            HubPinButton.Content = _hubPinned ? "\uE77A" : "\uE718";
            ToolTipService.SetToolTip(HubPinButton, _hubPinned ? "Unpin this pane" : "Pin this pane");
            ApplyHubPinLayout();
        }

        private bool CanDockHub()
        {
            var width = ActualWidth > 0 ? ActualWidth : Window.Current.Bounds.Width;
            return width >= SidePaneBorder.Width + 560;
        }

        private double GetDockedPaneWidth()
        {
            return _hubPinned && SidePanePopup.IsOpen && HubPanel.Visibility == Visibility.Visible && CanDockHub()
                ? SidePaneBorder.Width
                : 0;
        }

        private void ApplyHubPinLayout()
        {
            if (ContentArea == null) return;
            var dockedWidth = GetDockedPaneWidth();
            var margin = new Thickness(0, 0, dockedWidth, 0);
            foreach (FrameworkElement element in new FrameworkElement[]
            {
                MainToolbar, FavoritesBar, FindBar, NoteBar, ReadAloudBar, PdfBar, ContentArea, DownloadShelf
            })
            {
                if (element != null) element.Margin = margin;
            }

            HubPinButton.IsEnabled = _hubPinned || CanDockHub();
            UpdateAdaptiveChrome();
            if (OmniboxPopup.IsOpen) PositionOmniboxPopup();
            if (LegacySupportPopup.IsOpen) PositionLegacySupportPopup();
        }

        private async void ClearHub_Click(object sender, RoutedEventArgs e)
        {
            switch (_activeHub)
            {
                case "history":
                    ReplaceItems(_history, BrowserDataStore.ClearHistory());
                    break;
                case "downloads":
                    await DownloadCoordinator.Current.ClearTerminalAsync();
                    break;
                case "setaside":
                    ReplaceItems(_setAsideTabs, BrowserDataStore.ClearSetAsideTabs());
                    break;
                default:
                    return;
            }
            UpdateHubPresentation();
        }

        private async void HubList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as BrowserItem;
            if (item == null) return;
            if (_activeHub == "tabs")
            {
                if (Guid.TryParse(item.Token, out Guid id))
                {
                    var tab = _tabs.FirstOrDefault(candidate => candidate.Id == id);
                    if (tab != null) SwitchTab(tab);
                }
                if (!_hubPinned) SidePanePopup.IsOpen = false;
                return;
            }
            if (_activeHub == "downloads")
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(item.Token))
                    {
                        var file = await StorageApplicationPermissions.FutureAccessList.GetFileAsync(item.Token);
                        await Launcher.LaunchFileAsync(file);
                    }
                }
                catch { ShowTransientStatus("The downloaded file is no longer available."); }
                return;
            }
            if (_activeHub == "setaside")
            {
                ReplaceItems(_setAsideTabs, BrowserDataStore.RemoveSetAsideTab(item));
            }
            if (!_hubPinned) SidePanePopup.IsOpen = false;
            CreateTab(item.Url, true);
        }

        private void ClosePane_Click(object sender, RoutedEventArgs e) => SidePanePopup.IsOpen = false;

        private void UpdateSidePaneWidth()
        {
            if (SidePaneBorder == null) return;
            var availableWidth = ActualWidth > 0 ? ActualWidth : Window.Current.Bounds.Width;
            var preferredWidth = SettingsPanel?.Visibility == Visibility.Visible ? 600 : (IsHubEffectivelyExpanded() ? 562 : 480);
            SidePaneBorder.Width = Math.Max(320, Math.Min(preferredWidth, Math.Max(320, availableWidth)));
        }

        private void EnsurePinnedHubFits()
        {
            if (!_hubPinned || !SidePanePopup.IsOpen || HubPanel.Visibility != Visibility.Visible) return;

            if (_hubExpanded && !CanDockHub())
            {
                _hubExpanded = false;
                SetHubExpanded(false);
            }

            if (!CanDockHub()) SetHubPinned(false);
        }

        private void UpdateSettingsLayout(double paneWidth)
        {
            if (SettingsNavigationColumn == null || SettingsGeneralContent == null) return;
            var compact = paneWidth <= 520;
            SettingsNavigationColumn.Width = new GridLength(compact ? 0 : 190);
            SettingsNavigationRail.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            SettingsCompactHeader.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            var padding = compact ? new Thickness(20, 20, 20, 38) : new Thickness(28, 20, 28, 38);
            foreach (var panel in new[]
            {
                SettingsGeneralContent,
                SettingsPrivacyContent,
                SettingsPasswordsContent,
                SettingsAdvancedContent
            })
            {
                panel.Padding = padding;
            }
        }

        private void PositionSidePane()
        {
            if (SidePaneBorder == null) return;
            if (HubPanel?.Visibility == Visibility.Visible) ApplyHubExpansionVisual(IsHubEffectivelyExpanded());
            UpdateSidePaneWidth();
            EnsurePinnedHubFits();
            UpdateSettingsLayout(SidePaneBorder.Width);
            var titleHeight = TabPreviewBar?.Visibility == Visibility.Visible ? 178d : 40d;
            SidePaneBorder.Height = Math.Max(220, ActualHeight - titleHeight);
            SidePanePopup.HorizontalOffset = Math.Max(0, ActualWidth - SidePaneBorder.Width);
            SidePanePopup.VerticalOffset = titleHeight;
            ApplyHubPinLayout();
        }

        private void PositionLegacySupportPopup()
        {
            if (LegacySupportBorder == null || ActualWidth <= 0 || ActualHeight <= 0) return;
            var availableWidth = Math.Max(320, ActualWidth - GetDockedPaneWidth());
            var horizontalMargin = availableWidth >= 700 ? 26d : 0d;
            LegacySupportBorder.Width = Math.Max(320, availableWidth - (horizontalMargin * 2));
            LegacySupportPopup.HorizontalOffset = horizontalMargin;
            LegacySupportPopup.VerticalOffset = Math.Max(0, ActualHeight - GetDownloadShelfInset() - GetLegacySupportHeight());
        }

        private double GetDownloadShelfInset()
        {
            if (DownloadShelf?.Visibility != Visibility.Visible) return 0;
            return Math.Max(DownloadShelf.ActualHeight, DownloadShelf.MinHeight);
        }

        private double GetLegacySupportHeight()
        {
            if (LegacySupportBorder == null) return 0;
            return Math.Max(LegacySupportBorder.ActualHeight, LegacySupportBorder.MinHeight);
        }

        private void BottomNotification_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateBottomNotificationPositions();
        }

        private void UpdateBottomNotificationPositions()
        {
            UpdateLegacySupportPopup();
            if (OmniboxPopup?.IsOpen == true) PositionOmniboxPopup();
        }

        private void UpdateLegacySupportPopup()
        {
            var chromeHeight = (TabPreviewBar?.Visibility == Visibility.Visible ? 178d : 40d) + 53d;
            var enoughHeight = DownloadShelf?.Visibility != Visibility.Visible ||
                ActualHeight >= chromeHeight + GetDownloadShelfInset() + GetLegacySupportHeight() + 80d;
            LegacySupportPopup.IsOpen = BrowserDataStore.ShowLegacySupportNotice && !_isFullScreen && enoughHeight;
            if (LegacySupportPopup.IsOpen) PositionLegacySupportPopup();
            if (OmniboxPopup.IsOpen) PositionOmniboxPopup();
        }

        private void DismissLegacySupport_Click(object sender, RoutedEventArgs e)
        {
            BrowserDataStore.ShowLegacySupportNotice = false;
            _loadingSettings = true;
            LegacySupportNoticeToggle.IsOn = false;
            _loadingSettings = false;
            LegacySupportPopup.IsOpen = false;
        }

        private async void DownloadModernEdge_Click(object sender, RoutedEventArgs e)
        {
            var launched = await Launcher.LaunchUriAsync(new Uri("https://www.microsoft.com/edge/download"));
            DismissLegacySupport_Click(sender, e);
            if (!launched) ShowTransientStatus("The Microsoft Edge download page could not be opened.");
        }

        private void More_Click(object sender, RoutedEventArgs e)
        {
            CloseOmniboxPopup(false, true);
            MoreMenu.ShowAt(MoreButton);
        }

        private void MoreMenu_Opened(object sender, object e)
        {
            _moreMenuOpen = true;
        }

        private void MoreMenu_Closed(object sender, object e)
        {
            _moreMenuOpen = false;
            _activeMoreSubmenu = null;
        }

        private void MoreSubmenu_Opened(object sender, object e)
        {
            _activeMoreSubmenu = sender as Flyout;
        }

        private void MoreSubmenu_Closed(object sender, object e)
        {
            if (ReferenceEquals(_activeMoreSubmenu, sender)) _activeMoreSubmenu = null;
        }

        private async Task HideOpenFlyoutAsync(Flyout flyout, Func<bool> isOpen)
        {
            if (flyout == null || isOpen == null || !isOpen()) return;
            var closed = new TaskCompletionSource<bool>();
            EventHandler<object> closedHandler = (sender, args) => closed.TrySetResult(true);
            flyout.Closed += closedHandler;
            try
            {
                if (!isOpen()) return;
                await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    if (isOpen()) flyout.Hide();
                    else closed.TrySetResult(true);
                });
                if (!isOpen()) closed.TrySetResult(true);
                await Task.WhenAny(closed.Task, Task.Delay(750));
            }
            finally
            {
                flyout.Closed -= closedHandler;
            }
        }

        private async Task DismissActiveMoreSubmenuAsync()
        {
            var submenu = _activeMoreSubmenu;
            if (submenu != null)
                await HideOpenFlyoutAsync(submenu, () => ReferenceEquals(_activeMoreSubmenu, submenu));
            await HideOpenFlyoutAsync(MoreMenu, () => _moreMenuOpen);
        }

        private void FindKeyboardAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (!_initialized) return;
            args.Handled = true;
            OpenFindBar();
        }

        private void Find_Click(object sender, RoutedEventArgs e) => OpenFindBar();

        private void OpenFindBar()
        {
            if (_currentTab?.IsPdfView == true)
            {
                ShowTransientStatus("Text search isn't available for PDF documents in this version.");
                return;
            }

            try
            {
                if (_moreMenuOpen) MoreMenu.Hide();
            }
            catch { }

            var wasVisible = FindBar.Visibility == Visibility.Visible;
            FindBar.Visibility = Visibility.Visible;
            if (!string.IsNullOrEmpty(FindBox.Text) &&
                (!wasVisible || !string.Equals(FindBox.Text, _findQuery, StringComparison.Ordinal)))
                _findInitializationTask = InitializeFindAsync();
            _ = FocusFindBoxAsync();
        }

        private async Task FocusFindBoxAsync()
        {
            try
            {
                // Flyout dismissal completes on a later dispatcher turn. Moving
                // focus after it closes makes both the menu command and Ctrl+F
                // consistently place the caret in the find box.
                await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    if (FindBar.Visibility != Visibility.Visible) return;
                    FindBox.Focus(FocusState.Programmatic);
                    FindBox.SelectAll();
                });
            }
            catch { }
        }

        private void CloseFind_Click(object sender, RoutedEventArgs e)
        {
            var tab = _currentTab;
            var view = tab?.View;
            var viewGeneration = tab?.ViewGeneration ?? -1;
            var findGeneration = ++_findGeneration;
            _findInitializationTask = Task.CompletedTask;
            FindBar.Visibility = Visibility.Collapsed;
            _findQuery = string.Empty;
            _findMatchCount = 0;
            _findMatchIndex = 0;
            _findMatchCountTruncated = false;
            FindCountText.Text = "0 of 0";
            if (tab != null && _findHighlightedTabs.Contains(tab))
                _ = ClearFindHighlightsAsync(tab, view, viewGeneration, findGeneration);
            view?.Focus(FocusState.Programmatic);
        }

        private async void FindNext_Click(object sender, RoutedEventArgs e) => await FindOnPageAsync(false);
        private async void FindPrevious_Click(object sender, RoutedEventArgs e) => await FindOnPageAsync(true);
        private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (FindBar.Visibility == Visibility.Visible)
                _findInitializationTask = InitializeFindAsync();
        }

        private void FindBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                var backwards = (Window.Current.CoreWindow.GetKeyState(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;
                _ = FindOnPageAsync(backwards);
            }
            else if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                CloseFind_Click(sender, e);
            }
        }

        private async Task<bool> TryBeginFindOperationAsync(BrowserTab tab, WebView view, int viewGeneration, int findGeneration)
        {
            for (var attempt = 0; attempt < 7; attempt++)
            {
                if (!IsLiveWebView(tab, view, viewGeneration) || tab != _currentTab ||
                    (findGeneration >= 0 && findGeneration != _findGeneration))
                    return false;
                if (BrowserHost.Children.Contains(view) && TryBeginWebViewCapture(view)) return true;
                await Task.Delay(40);
            }
            return false;
        }

        private async Task InitializeFindAsync()
        {
            var tab = _currentTab;
            var view = tab?.View;
            var viewGeneration = tab?.ViewGeneration ?? -1;
            var query = FindBox.Text ?? string.Empty;
            var generation = ++_findGeneration;
            _findQuery = query;
            _findMatchCount = 0;
            _findMatchIndex = 0;
            _findMatchCountTruncated = false;
            FindCountText.Text = "0 of 0";

            if (tab == null || view == null || tab.IsPdfView) return;
            if (query.Length == 0)
            {
                if (_findHighlightedTabs.Contains(tab))
                    await ClearFindHighlightsAsync(tab, view, viewGeneration, generation);
                return;
            }

            // Coalesce rapid TextChanged notifications. The generation check
            // prevents an older query from updating or leaving highlights after
            // the user types again, navigates, closes Find, or switches tabs.
            await Task.Delay(120);
            if (generation != _findGeneration || tab != _currentTab || FindBar.Visibility != Visibility.Visible) return;
            if (!await TryBeginFindOperationAsync(tab, view, viewGeneration, generation))
            {
                if (generation == _findGeneration && tab == _currentTab)
                {
                    _findQuery = string.Empty;
                    FindCountText.Text = "Try again";
                }
                return;
            }

            try
            {
                var result = await view.InvokeScriptAsync("eval", new[] { BuildFindInitializationScript(query) });
                if (generation != _findGeneration || tab != _currentTab || !IsLiveWebView(tab, view, viewGeneration))
                {
                    try { await view.InvokeScriptAsync("eval", new[] { BuildClearFindHighlightsScript() }); }
                    catch { }
                    _findHighlightedTabs.Remove(tab);
                    return;
                }

                if (!TryParseFindResult(result, out int count, out int index, out bool truncated))
                {
                    try { await view.InvokeScriptAsync("eval", new[] { BuildClearFindHighlightsScript() }); }
                    catch { }
                    _findHighlightedTabs.Remove(tab);
                    _findQuery = string.Empty;
                    FindCountText.Text = "Unavailable";
                    return;
                }
                _findMatchCount = count;
                _findMatchIndex = index;
                _findMatchCountTruncated = truncated;
                if (count > 0) _findHighlightedTabs.Add(tab); else _findHighlightedTabs.Remove(tab);
                UpdateFindStatus();
            }
            catch
            {
                try { await view.InvokeScriptAsync("eval", new[] { BuildClearFindHighlightsScript() }); }
                catch { }
                _findHighlightedTabs.Remove(tab);
                if (generation == _findGeneration && tab == _currentTab)
                {
                    _findQuery = string.Empty;
                    FindCountText.Text = "Unavailable";
                }
            }
            finally
            {
                EndWebViewCapture(view);
            }
        }

        private async Task FindOnPageAsync(bool backwards)
        {
            var query = FindBox.Text ?? string.Empty;
            if (_currentTab == null || query.Length == 0) return;

            if (!string.Equals(query, _findQuery, StringComparison.Ordinal))
                _findInitializationTask = InitializeFindAsync();
            var initialization = _findInitializationTask;
            if (initialization != null && !initialization.IsCompleted)
                await initialization;

            var tab = _currentTab;
            var view = tab?.View;
            var viewGeneration = tab?.ViewGeneration ?? -1;
            var generation = _findGeneration;
            if (tab == null || view == null || tab.IsPdfView || query != (FindBox.Text ?? string.Empty) ||
                !string.Equals(query, _findQuery, StringComparison.Ordinal))
                return;
            if (!await TryBeginFindOperationAsync(tab, view, viewGeneration, generation)) return;

            try
            {
                var result = await view.InvokeScriptAsync("eval", new[] { BuildFindNavigationScript(backwards) });
                if (generation != _findGeneration || tab != _currentTab || !IsLiveWebView(tab, view, viewGeneration)) return;
                if (!TryParseFindResult(result, out int count, out int index, out bool truncated))
                {
                    FindCountText.Text = "Unavailable";
                    return;
                }
                _findMatchCount = count;
                _findMatchIndex = index;
                _findMatchCountTruncated = truncated;
                if (count > 0) _findHighlightedTabs.Add(tab); else _findHighlightedTabs.Remove(tab);
                UpdateFindStatus();
            }
            catch
            {
                if (generation == _findGeneration && tab == _currentTab) FindCountText.Text = "Unavailable";
            }
            finally
            {
                EndWebViewCapture(view);
            }
        }

        private async Task ClearFindHighlightsAsync(BrowserTab tab, WebView view, int viewGeneration, int findGeneration)
        {
            if (tab == null || view == null || !IsLiveWebView(tab, view, viewGeneration) || tab != _currentTab) return;
            if (!await TryBeginFindOperationAsync(tab, view, viewGeneration, findGeneration)) return;
            try
            {
                await view.InvokeScriptAsync("eval", new[] { BuildClearFindHighlightsScript() });
                _findHighlightedTabs.Remove(tab);
            }
            catch { }
            finally { EndWebViewCapture(view); }
        }

        private async Task ClearFindHighlightsAndRevealAsync(BrowserTab tab, WebView view, int viewGeneration, int findGeneration)
        {
            try
            {
                await ClearFindHighlightsAsync(tab, view, viewGeneration, findGeneration);
            }
            finally
            {
                if (tab == _currentTab && IsLiveWebView(tab, view, viewGeneration) &&
                    BrowserHost.Children.Contains(view))
                    view.Visibility = Visibility.Visible;
            }
        }

        private static bool TryParseFindResult(string value, out int count, out int index, out bool truncated)
        {
            count = 0;
            index = 0;
            truncated = false;
            var parts = (value ?? string.Empty).Split('|');
            if (parts.Length < 2 || !int.TryParse(parts[0], out count) || !int.TryParse(parts[1], out index)) return false;
            count = Math.Max(0, count);
            index = Math.Max(0, Math.Min(index, count));
            truncated = parts.Length > 2 && parts[2] == "1";
            return true;
        }

        private static string BuildClearFindHighlightsScript()
        {
            return "(function(){" +
                "var marks=document.querySelectorAll('span[data-legacy-edge-find=\"1\"]'),parents=[],i,m,p;" +
                "for(i=marks.length-1;i>=0;i--){m=marks[i];p=m.parentNode;if(!p)continue;p.replaceChild(document.createTextNode(m.textContent||''),m);if(parents.indexOf(p)<0)parents.push(p);}" +
                "for(i=0;i<parents.length;i++){try{parents[i].normalize();}catch(ignore){}}" +
                "window.__legacyEdgeFind=null;return '0|0|0';})()";
        }

        private static string BuildFindInitializationScript(string query)
        {
            var queryLiteral = JsonValue.CreateStringValue(query ?? string.Empty).Stringify();
            return "(function(q){" +
                "function clear(){var a=document.querySelectorAll('span[data-legacy-edge-find=\"1\"]'),ps=[],i,m,p;for(i=a.length-1;i>=0;i--){m=a[i];p=m.parentNode;if(!p)continue;p.replaceChild(document.createTextNode(m.textContent||''),m);if(ps.indexOf(p)<0)ps.push(p);}for(i=0;i<ps.length;i++){try{ps[i].normalize();}catch(ignore){}}window.__legacyEdgeFind=null;}" +
                "function paint(el,on){if(!el)return;el.style.setProperty('background-color',on?'#ffb900':'#fff59d','important');el.style.setProperty('color','#000','important');el.style.setProperty('outline',on?'1px solid #9b5c00':'none','important');el.style.setProperty('display','inline','important');}" +
                "function reveal(el){try{el.scrollIntoView(false);}catch(ignore){}}" +
                "clear();q=String(q||'');if(!q)return '0|0|0';" +
                "var root=document.body||document.documentElement;if(!root)return '0|0|0';" +
                "var lower=q.toLocaleLowerCase(),walker=document.createTreeWalker(root,4,{acceptNode:function(node){var p=node.parentNode,e,tag,style;if(!p||!node.nodeValue||!node.nodeValue.replace(/\\s/g,''))return 2;for(e=p;e&&e.nodeType===1;e=e.parentNode){tag=(e.tagName||'').toUpperCase();if(tag==='SCRIPT'||tag==='STYLE'||tag==='NOSCRIPT'||tag==='TEXTAREA'||tag==='INPUT'||tag==='SELECT'||tag==='OPTION'||tag==='IFRAME'||tag==='OBJECT'||tag==='EMBED'||tag==='CANVAS')return 2;if(e.getAttribute&&e.getAttribute('aria-hidden')==='true')return 2;if(e===root)break;}try{style=window.getComputedStyle?window.getComputedStyle(p,null):p.currentStyle;if(style&&(style.display==='none'||style.visibility==='hidden'))return 2;if(p.getClientRects&&p.getClientRects().length===0)return 2;}catch(ignore){}return 1;}},false);" +
                "var nodes=[],node,scanTruncated=false,started=Date.now();while((node=walker.nextNode())){nodes.push(node);if(nodes.length>=50000||Date.now()-started>150){scanTruncated=true;break;}}" +
                "var state={matches:[],index:-1,truncated:scanTruncated,query:q},limit=2000,stop=false,n,text,low,cursor,pos,frag,mark;" +
                "for(n=0;n<nodes.length&&!stop;n++){if(Date.now()-started>350){state.truncated=true;break;}node=nodes[n];text=node.nodeValue;low=text.toLocaleLowerCase();cursor=0;frag=document.createDocumentFragment();while((pos=low.indexOf(lower,cursor))>=0){if(state.matches.length>=limit||Date.now()-started>350){state.truncated=true;stop=true;break;}if(pos>cursor)frag.appendChild(document.createTextNode(text.substring(cursor,pos)));mark=document.createElement('span');mark.setAttribute('data-legacy-edge-find','1');mark.appendChild(document.createTextNode(text.substr(pos,q.length)));paint(mark,false);frag.appendChild(mark);state.matches.push(mark);cursor=pos+q.length;}if(cursor>0){frag.appendChild(document.createTextNode(text.substring(cursor)));if(node.parentNode)node.parentNode.replaceChild(frag,node);}}" +
                "window.__legacyEdgeFind=state;if(state.matches.length){state.index=0;paint(state.matches[0],true);reveal(state.matches[0]);}" +
                "return String(state.matches.length)+'|'+String(state.index+1)+'|'+(state.truncated?'1':'0');})(" + queryLiteral + ")";
        }

        private static string BuildFindNavigationScript(bool backwards)
        {
            return "(function(step){" +
                "function paint(el,on){if(!el)return;el.style.setProperty('background-color',on?'#ffb900':'#fff59d','important');el.style.setProperty('color','#000','important');el.style.setProperty('outline',on?'1px solid #9b5c00':'none','important');}" +
                "var s=window.__legacyEdgeFind;if(!s||!s.matches)return '0|0|0';var active=s.index>=0?s.matches[s.index]:null,live=[],i;if(active&&active.parentNode)paint(active,false);for(i=0;i<s.matches.length;i++)if(s.matches[i]&&s.matches[i].parentNode)live.push(s.matches[i]);s.matches=live;if(!live.length){s.index=-1;return '0|0|'+(s.truncated?'1':'0');}" +
                "i=active?live.indexOf(active):-1;if(i<0)i=step<0?0:-1;s.index=(i+step+live.length)%live.length;active=live[s.index];paint(active,true);try{active.scrollIntoView(false);}catch(ignore){}return String(live.length)+'|'+String(s.index+1)+'|'+(s.truncated?'1':'0');})(" + (backwards ? "-1" : "1") + ")";
        }

        private void UpdateFindStatus()
        {
            if (_findMatchCount <= 0)
            {
                FindCountText.Text = "0 of 0";
                return;
            }
            FindCountText.Text = _findMatchIndex + " of " + _findMatchCount + (_findMatchCountTruncated ? "+" : string.Empty);
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e) { MoreMenu.Hide(); ChangeZoom(10); }
        private void ZoomOut_Click(object sender, RoutedEventArgs e) { MoreMenu.Hide(); ChangeZoom(-10); }
        private void ResetZoom_Click(object sender, RoutedEventArgs e)
        {
            MoreMenu.Hide();
            if (_currentTab == null) return;
            if (_currentTab.IsPdfView) SetPdfZoom(_currentTab, 1, false);
            else
            {
                _currentTab.ZoomPercent = 100;
                _ = ApplyZoomAsync(_currentTab);
                UpdateChrome();
            }
        }

        private void ChangeZoom(double amount)
        {
            if (_currentTab == null) return;
            if (_currentTab.IsPdfView)
            {
                SetPdfZoom(_currentTab, _currentTab.PdfZoom + (amount / 100d), false);
                return;
            }
            _currentTab.ZoomPercent = Math.Max(25, Math.Min(500, _currentTab.ZoomPercent + amount));
            _ = ApplyZoomAsync(_currentTab);
            UpdateChrome();
        }

        private async Task ApplyZoomAsync(BrowserTab tab, WebView expectedView = null)
        {
            try
            {
                var view = expectedView ?? tab?.View;
                if (view == null) return;
                var script = "document.documentElement.style.zoom='" + ((int)tab.ZoomPercent) + "%'; 'ok'";
                await view.InvokeScriptAsync("eval", new[] { script });
            }
            catch { }
        }

        private static bool LooksLikePdf(Uri uri, string mediaType)
        {
            if (!string.IsNullOrWhiteSpace(mediaType) && mediaType.IndexOf("pdf", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (uri == null) return false;
            return string.Equals(Path.GetExtension(uri.AbsolutePath), ".pdf", StringComparison.OrdinalIgnoreCase);
        }

        private async void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            await DismissActiveMoreSubmenuAsync();
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add(".pdf");
            picker.FileTypeFilter.Add(".html");
            picker.FileTypeFilter.Add(".htm");
            picker.FileTypeFilter.Add(".svg");
            picker.FileTypeFilter.Add(".txt");
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            var tab = _currentTab ?? CreateTab(null, true, false, false);
            await OpenLocalFileAsync(tab, file);
        }

        private async Task OpenStoredFileAsync(BrowserTab tab, string token)
        {
            if (tab == null || string.IsNullOrWhiteSpace(token)) return;
            var navigationGeneration = ++tab.NavigationGeneration;
            try
            {
                var file = await StorageApplicationPermissions.FutureAccessList.GetFileAsync(token);
                if (!_tabs.Contains(tab) || tab.NavigationGeneration != navigationGeneration) return;
                await OpenLocalFileAsync(tab, file, token);
            }
            catch
            {
                if (!_tabs.Contains(tab) || tab.NavigationGeneration != navigationGeneration) return;
                ShowTransientStatus("This local file is no longer available. Open it again to restore access.");
                ShowStartPage(tab);
            }
        }

        private async Task OpenLocalFileAsync(BrowserTab tab, StorageFile file, string existingToken = null)
        {
            if (tab == null || file == null || !_tabs.Contains(tab)) return;
            var navigationGeneration = ++tab.NavigationGeneration;
            var extension = Path.GetExtension(file.Name).ToLowerInvariant();
            if (extension != ".pdf" && extension != ".html" && extension != ".htm" && extension != ".svg" && extension != ".txt")
            {
                ShowTransientStatus("Microsoft Edge cannot display this file type.");
                return;
            }

            ClearPdfState(tab);
            ClearLocalFileState(tab);
            var token = existingToken;
            try
            {
                if (string.IsNullOrWhiteSpace(token)) token = StorageApplicationPermissions.FutureAccessList.Add(file, file.Name);
                else if (!StorageApplicationPermissions.FutureAccessList.ContainsItem(token)) StorageApplicationPermissions.FutureAccessList.AddOrReplace(token, file, file.Name);
            }
            catch { token = null; }
            tab.LocalFile = file;
            tab.LocalFileToken = token;

            if (extension == ".pdf")
            {
                tab.PreviousAddress = tab.Address;
                tab.PreviousTitle = tab.Title;
                await OpenLocalPdfAsync(tab, file);
                return;
            }

            try
            {
                tab.PreviousAddress = tab.Address;
                tab.PreviousTitle = tab.Title;
                tab.Address = file.Path;
                tab.Title = file.Name;
                tab.IconUrl = null;
                tab.IsLoading = true;
                tab.PreviewImage = null;
                var source = await FileIO.ReadTextAsync(file);
                if (!_tabs.Contains(tab) || tab.NavigationGeneration != navigationGeneration) return;
                if (extension == ".txt")
                    source = "<!doctype html><html><head><meta charset='utf-8'><title>" + HtmlEncode(file.Name) + "</title><style>body{margin:0;padding:24px;background:#fff;color:#222;font:14px/1.55 Consolas,monospace;white-space:pre-wrap}</style></head><body>" + HtmlEncode(source) + "</body></html>";
                else if (extension == ".svg")
                    source = "<!doctype html><html><head><meta charset='utf-8'><title>" + HtmlEncode(file.Name) + "</title><style>html,body{margin:0;min-height:100%;background:#fff}body{display:flex;align-items:flex-start;justify-content:center;padding:18px;box-sizing:border-box}svg{max-width:100%;height:auto}</style></head><body>" + source + "</body></html>";

                if (tab == _currentTab)
                {
                    HidePdfSurface();
                    tab.View.Visibility = Visibility.Visible;
                }
                tab.View.NavigateToString(source);
                UpdateSessionSnapshot();
                if (tab == _currentTab) UpdateChrome();
                RebuildTabs();
            }
            catch
            {
                if (!_tabs.Contains(tab) || tab.NavigationGeneration != navigationGeneration) return;
                tab.IsLoading = false;
                ShowTransientStatus("This local file could not be opened.");
            }
        }

        private async Task OpenLocalPdfAsync(BrowserTab tab, StorageFile file)
        {
            if (tab == null || file == null || !_tabs.Contains(tab)) return;
            ++tab.NavigationGeneration;
            var loadGeneration = ++tab.PdfLoadGeneration;
            if (!tab.IsPdfView)
            {
                tab.PdfReturnAddress = tab.PreviousAddress ?? string.Empty;
                tab.PdfReturnTitle = tab.PreviousTitle ?? "New tab";
                tab.PdfZoom = 1;
                tab.PdfFitWidth = true;
                tab.PdfRotation = 0;
                tab.PdfCurrentPage = 1;
                tab.PdfVerticalOffset = 0;
            }
            tab.IsPdfView = true;
            tab.PdfSourceAddress = file.Path;
            tab.Address = file.Path;
            tab.Title = file.Name;
            tab.PdfFile = file;
            tab.PdfDocument = null;
            tab.PdfPageImages.Clear();
            tab.IsLoading = true;
            tab.PreviewImage = null;

            if (tab == _currentTab)
            {
                StopReadAloud();
                if (NoteSurface.Visibility == Visibility.Visible) ExitNoteMode();
                ShowPdfTab(tab);
                UpdateChrome();
                RebuildTabs();
            }

            try
            {
                var document = await PdfDocument.LoadFromFileAsync(file);
                if (!_tabs.Contains(tab) || loadGeneration != tab.PdfLoadGeneration || !tab.IsPdfView) return;
                tab.PdfDocument = document;
                tab.IsLoading = false;
                tab.PdfCurrentPage = 1;
                if (tab.PdfFitWidth) tab.PdfZoom = CalculatePdfFitZoom(tab);
                if (tab == _currentTab)
                {
                    ShowPdfTab(tab);
                    UpdateChrome();
                    RebuildTabs();
                }
                UpdateSessionSnapshot();
            }
            catch
            {
                if (!_tabs.Contains(tab) || loadGeneration != tab.PdfLoadGeneration || !tab.IsPdfView) return;
                tab.IsLoading = false;
                tab.PdfDocument = null;
                if (tab == _currentTab)
                {
                    ShowPdfTab(tab);
                    UpdateChrome();
                    ShowTransientStatus("This local PDF could not be opened. It may be damaged or password protected.");
                }
            }
        }

        private void ClearLocalFileState(BrowserTab tab)
        {
            if (tab == null) return;
            var token = tab.LocalFileToken;
            tab.LocalFile = null;
            tab.LocalFileToken = null;
            if (string.IsNullOrWhiteSpace(token)) return;
            var storedAddress = "legacyedge-file:" + token;
            var stillReferenced = _tabs.Any(item => item != tab && string.Equals(item.LocalFileToken, token, StringComparison.Ordinal)) ||
                _closedTabs.Any(item => string.Equals(item, storedAddress, StringComparison.Ordinal)) ||
                _setAsideTabs.Any(item => string.Equals(item.Url, storedAddress, StringComparison.Ordinal)) ||
                BrowserDataStore.LoadSession().Any(item => string.Equals(item, storedAddress, StringComparison.Ordinal));
            if (stillReferenced) return;
            try
            {
                if (StorageApplicationPermissions.FutureAccessList.ContainsItem(token)) StorageApplicationPermissions.FutureAccessList.Remove(token);
            }
            catch { }
        }

        private async Task OpenPdfAsync(BrowserTab tab, Uri uri)
        {
            if (tab == null || uri == null || !_tabs.Contains(tab)) return;
            ++tab.NavigationGeneration;
            var loadGeneration = ++tab.PdfLoadGeneration;
            if (!tab.IsPdfView)
            {
                var navigationAlreadyReachedPdf = string.Equals(tab.Address, uri.ToString(), StringComparison.OrdinalIgnoreCase);
                tab.PdfReturnAddress = navigationAlreadyReachedPdf && tab.PreviousAddress != null ? tab.PreviousAddress : tab.Address;
                tab.PdfReturnTitle = navigationAlreadyReachedPdf && tab.PreviousTitle != null ? tab.PreviousTitle : tab.Title;
                tab.PdfZoom = 1;
                tab.PdfFitWidth = true;
                tab.PdfRotation = 0;
                tab.PdfCurrentPage = 1;
                tab.PdfVerticalOffset = 0;
            }

            var sourceChanged = !string.Equals(tab.PdfSourceAddress, uri.ToString(), StringComparison.OrdinalIgnoreCase);
            tab.IsPdfView = true;
            tab.PdfSourceAddress = uri.ToString();
            tab.Address = uri.ToString();
            tab.IsLoading = true;
            tab.PreviewImage = null;
            if (sourceChanged)
            {
                tab.PdfDocument = null;
                tab.PdfFile = null;
                tab.PdfPageImages.Clear();
            }

            var fileName = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "document.pdf";
            if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) fileName += ".pdf";
            fileName = SafeFileName(fileName);
            tab.Title = fileName;

            if (tab == _currentTab)
            {
                StopReadAloud();
                if (NoteSurface.Visibility == Visibility.Visible) ExitNoteMode();
                ShowPdfTab(tab);
                UpdateChrome();
                RebuildTabs();
            }

            try
            {
                var file = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(fileName, CreationCollisionOption.GenerateUniqueName);
                using (var client = new HttpClient())
                using (var response = await client.GetAsync(uri))
                {
                    response.EnsureSuccessStatusCode();
                    using (var input = await response.Content.ReadAsInputStreamAsync())
                    using (var output = await file.OpenAsync(FileAccessMode.ReadWrite))
                    {
                        await RandomAccessStream.CopyAsync(input, output);
                        await output.FlushAsync();
                    }
                }

                var document = await PdfDocument.LoadFromFileAsync(file);
                if (!_tabs.Contains(tab) || loadGeneration != tab.PdfLoadGeneration || !tab.IsPdfView) return;
                tab.PdfFile = file;
                tab.PdfDocument = document;
                tab.IsLoading = false;
                tab.PdfPageImages.Clear();
                tab.PdfCurrentPage = Math.Max(1u, Math.Min(tab.PdfCurrentPage, Math.Max(1u, document.PageCount)));
                if (tab.PdfFitWidth) tab.PdfZoom = CalculatePdfFitZoom(tab);
                if (!tab.IsPrivate) AddHistory(tab.Title, tab.Address, null);

                if (tab == _currentTab)
                {
                    ShowPdfTab(tab);
                    UpdateChrome();
                    RebuildTabs();
                }
                UpdateSessionSnapshot();
            }
            catch
            {
                if (!_tabs.Contains(tab) || loadGeneration != tab.PdfLoadGeneration || !tab.IsPdfView) return;
                tab.IsLoading = false;
                tab.PdfDocument = null;
                if (tab == _currentTab)
                {
                    ShowPdfTab(tab);
                    UpdateChrome();
                    ShowTransientStatus("This PDF could not be opened. It may require credentials or a password.");
                }
            }
        }

        private void ShowPdfTab(BrowserTab tab)
        {
            if (tab == null || tab != _currentTab || !tab.IsPdfView) return;
            PdfSurface.Visibility = Visibility.Visible;
            PdfBar.Visibility = Visibility.Visible;
            DetachDormantWebView(tab.View);
            PdfTitleText.Text = string.IsNullOrWhiteSpace(tab.Title) ? "PDF document" : tab.Title;
            PdfLoadingOverlay.Visibility = tab.PdfDocument == null ? Visibility.Visible : Visibility.Collapsed;
            PdfLoadingRing.IsActive = tab.IsLoading;
            PdfLoadingText.Text = tab.IsLoading ? "Opening PDF…" : "This PDF could not be displayed.";
            UpdatePdfToolbar(tab);

            if (tab.PdfDocument != null && (_pdfVisualTabId != tab.Id || (uint)PdfPagesPanel.Children.Count != tab.PdfDocument.PageCount))
                BuildPdfPages(tab);
            else if (tab.PdfDocument != null)
                _ = RenderVisiblePdfPagesAsync();
        }

        private void HidePdfSurface()
        {
            if (_currentTab?.IsPdfView == true) _currentTab.PdfVerticalOffset = PdfScrollViewer.VerticalOffset;
            ++_pdfRenderGeneration;
            _pdfVisualTabId = Guid.Empty;
            _pdfPageVisuals.Clear();
            PdfPagesPanel?.Children.Clear();
            if (PdfSurface != null) PdfSurface.Visibility = Visibility.Collapsed;
            if (PdfBar != null) PdfBar.Visibility = Visibility.Collapsed;
            if (PdfLoadingRing != null) PdfLoadingRing.IsActive = false;
        }

        private void ClearPdfState(BrowserTab tab)
        {
            if (tab == null || !tab.IsPdfView) return;
            var returnAddress = tab.PdfReturnAddress;
            var returnTitle = tab.PdfReturnTitle;
            tab.IsPdfView = false;
            tab.PdfLoadGeneration++;
            tab.IsLoading = false;
            tab.PdfSourceAddress = null;
            tab.PdfDocument = null;
            tab.PdfFile = null;
            tab.PdfPageImages.Clear();
            tab.PdfReturnAddress = null;
            tab.PdfReturnTitle = null;
            tab.PdfReturnNeedsReload = false;
            tab.PdfVerticalOffset = 0;
            if (returnAddress != null) tab.Address = returnAddress;
            if (!string.IsNullOrWhiteSpace(returnTitle)) tab.Title = returnTitle;
            if (tab == _currentTab)
            {
                HidePdfSurface();
                if (tab.View != null)
                {
                    if (!BrowserHost.Children.Contains(tab.View)) BrowserHost.Children.Add(tab.View);
                    tab.View.Visibility = Visibility.Visible;
                }
            }
        }

        private double CalculatePdfFitZoom(BrowserTab tab)
        {
            if (tab?.PdfDocument == null || tab.PdfDocument.PageCount == 0) return 1;
            try
            {
                using (var page = tab.PdfDocument.GetPage(0))
                {
                    var width = tab.PdfRotation % 180 == 0 ? page.Size.Width : page.Size.Height;
                    var available = Math.Max(320, (PdfSurface.ActualWidth > 0 ? PdfSurface.ActualWidth : ActualWidth) - 76);
                    return Math.Max(0.25, Math.Min(4, available / Math.Max(1, width)));
                }
            }
            catch { return 1; }
        }

        private void BuildPdfPages(BrowserTab tab)
        {
            if (tab?.PdfDocument == null || tab != _currentTab) return;
            var rasterScale = GetPdfRasterScale();
            if (Math.Abs(tab.PdfRasterScale - rasterScale) > 0.05)
            {
                tab.PdfRasterScale = rasterScale;
                tab.PdfPageImages.Clear();
            }
            var generation = ++_pdfRenderGeneration;
            _pdfVisualTabId = tab.Id;
            _pdfPageVisuals.Clear();
            PdfPagesPanel.Children.Clear();

            for (uint index = 0; index < tab.PdfDocument.PageCount; index++)
            {
                Size pageSize;
                try
                {
                    using (var page = tab.PdfDocument.GetPage(index)) pageSize = page.Size;
                }
                catch { pageSize = new Size(816, 1056); }

                var unrotatedWidth = Math.Max(160, pageSize.Width * tab.PdfZoom);
                var unrotatedHeight = Math.Max(200, pageSize.Height * tab.PdfZoom);
                var sideways = tab.PdfRotation % 180 != 0;
                var displayWidth = sideways ? unrotatedHeight : unrotatedWidth;
                var displayHeight = sideways ? unrotatedWidth : unrotatedHeight;
                var canvas = new Grid
                {
                    Width = displayWidth,
                    Height = displayHeight,
                    Background = new SolidColorBrush(Colors.White)
                };
                var placeholder = new TextBlock
                {
                    Text = "Page " + (index + 1),
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 120, 120, 120)),
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var image = new Image
                {
                    Width = unrotatedWidth,
                    Height = unrotatedHeight,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new RotateTransform { Angle = tab.PdfRotation }
                };
                if (tab.PdfPageImages.TryGetValue(index, out BitmapImage cached))
                {
                    image.Source = cached;
                    placeholder.Visibility = Visibility.Collapsed;
                }
                canvas.Children.Add(placeholder);
                canvas.Children.Add(image);

                var host = new Border
                {
                    Child = canvas,
                    Background = new SolidColorBrush(Colors.White),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(255, 70, 70, 70)),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(20, 0, 20, 18),
                    Tag = index
                };
                PdfPagesPanel.Children.Add(host);
                _pdfPageVisuals[index] = new PdfPageVisual { PageIndex = index, Host = host, Canvas = canvas, Image = image, Placeholder = placeholder };
            }

            UpdatePdfToolbar(tab);
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                if (generation != _pdfRenderGeneration || tab != _currentTab) return;
                PdfScrollViewer.ChangeView(null, tab.PdfVerticalOffset, null, true);
                _ = RenderVisiblePdfPagesAsync();
            });
        }

        private async Task RenderVisiblePdfPagesAsync()
        {
            var tab = _currentTab;
            if (_pdfRenderBusy || tab?.IsPdfView != true || tab.PdfDocument == null || PdfSurface.Visibility != Visibility.Visible) return;
            _pdfRenderBusy = true;
            var generation = _pdfRenderGeneration;
            try
            {
                var candidates = new List<PdfPageVisual>();
                var nearbyPages = new HashSet<uint>();
                PdfPageVisual currentVisual = null;
                var currentDistance = double.MaxValue;
                foreach (var visual in _pdfPageVisuals.Values)
                {
                    Point position;
                    try { position = visual.Host.TransformToVisual(PdfScrollViewer).TransformPoint(new Point(0, 0)); }
                    catch { continue; }
                    var height = visual.Host.ActualHeight > 0 ? visual.Host.ActualHeight : visual.Canvas.Height;
                    var distance = Math.Abs(position.Y - 26);
                    if (distance < currentDistance)
                    {
                        currentDistance = distance;
                        currentVisual = visual;
                    }
                    if (position.Y < PdfScrollViewer.ActualHeight + 650 && position.Y + height > -650)
                    {
                        nearbyPages.Add(visual.PageIndex);
                        if (visual.Image.Source == null) candidates.Add(visual);
                    }
                }

                if (currentVisual != null)
                {
                    tab.PdfCurrentPage = currentVisual.PageIndex + 1;
                    PdfPageBox.Text = tab.PdfCurrentPage.ToString();
                }
                if (candidates.Count == 0 && _pdfPageVisuals.TryGetValue(tab.PdfCurrentPage - 1, out PdfPageVisual selected) && selected.Image.Source == null)
                    candidates.Add(selected);

                foreach (var visual in candidates.OrderBy(item => Math.Abs((long)item.PageIndex - tab.PdfCurrentPage)))
                {
                    if (generation != _pdfRenderGeneration || tab != _currentTab) return;
                    await RenderPdfPageAsync(tab, visual, generation);
                }
                TrimPdfPageCache(tab, nearbyPages);
            }
            finally
            {
                _pdfRenderBusy = false;
                if (generation != _pdfRenderGeneration && _currentTab?.IsPdfView == true) _ = RenderVisiblePdfPagesAsync();
            }
        }

        private async Task RenderPdfPageAsync(BrowserTab tab, PdfPageVisual visual, int generation)
        {
            if (tab.PdfPageImages.TryGetValue(visual.PageIndex, out BitmapImage cached))
            {
                visual.Image.Source = cached;
                visual.Placeholder.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                using (var page = tab.PdfDocument.GetPage(visual.PageIndex))
                using (var stream = new InMemoryRandomAccessStream())
                {
                    var pixelWidth = (uint)Math.Max(320, Math.Min(3200, Math.Round(page.Size.Width * tab.PdfZoom * GetPdfRasterScale() * 1.05)));
                    var options = new PdfPageRenderOptions
                    {
                        DestinationWidth = pixelWidth,
                        BackgroundColor = Colors.White,
                        IsIgnoringHighContrast = true
                    };
                    await page.RenderToStreamAsync(stream, options);
                    stream.Seek(0);
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    tab.PdfPageImages[visual.PageIndex] = bitmap;
                    if (generation != _pdfRenderGeneration || tab != _currentTab) return;
                    visual.Image.Source = bitmap;
                    visual.Placeholder.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                visual.Placeholder.Text = "Page " + (visual.PageIndex + 1) + " could not be rendered";
            }
        }

        private static double GetPdfRasterScale()
        {
            try { return Math.Max(1, DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel); }
            catch { return 1; }
        }

        private void TrimPdfPageCache(BrowserTab tab, IEnumerable<uint> nearbyPages)
        {
            if (tab == null || tab.PdfPageImages.Count <= 6) return;
            var currentIndex = tab.PdfCurrentPage > 0 ? tab.PdfCurrentPage - 1 : 0;
            var keep = new HashSet<uint>(nearbyPages ?? Enumerable.Empty<uint>());
            foreach (var index in tab.PdfPageImages.Keys.OrderBy(index => Math.Abs((long)index - currentIndex)))
            {
                if (keep.Count >= 6) break;
                keep.Add(index);
            }

            foreach (var index in tab.PdfPageImages.Keys.Where(index => !keep.Contains(index)).ToList())
            {
                tab.PdfPageImages.Remove(index);
                if (_pdfPageVisuals.TryGetValue(index, out PdfPageVisual visual))
                {
                    visual.Image.Source = null;
                    visual.Placeholder.Visibility = Visibility.Visible;
                }
            }
        }

        private void SetPdfZoom(BrowserTab tab, double zoom, bool fitWidth)
        {
            if (tab?.PdfDocument == null) return;
            tab.PdfCurrentPage = Math.Max(1u, tab.PdfCurrentPage);
            tab.PdfZoom = Math.Max(0.25, Math.Min(4, zoom));
            tab.PdfFitWidth = fitWidth;
            tab.PdfPageImages.Clear();
            tab.PdfVerticalOffset = 0;
            BuildPdfPages(tab);
            UpdateChrome();
        }

        private void UpdatePdfToolbar(BrowserTab tab)
        {
            if (tab == null) return;
            PdfTitleText.Text = string.IsNullOrWhiteSpace(tab.Title) ? "PDF document" : tab.Title;
            PdfPageBox.Text = Math.Max(1u, tab.PdfCurrentPage).ToString();
            PdfPageCountText.Text = "/ " + (tab.PdfDocument?.PageCount ?? 0);
            var zoomText = ((int)Math.Round(tab.PdfZoom * 100)) + "%";
            PdfZoomText.Content = zoomText;
            PdfOverflowZoomText.Content = zoomText;
            PdfFitButton.Foreground = new SolidColorBrush(tab.PdfFitWidth ? Color.FromArgb(255, 64, 156, 255) : Colors.White);
        }

        private void GoToPdfPage(BrowserTab tab, uint pageNumber)
        {
            if (tab?.PdfDocument == null || tab.PdfDocument.PageCount == 0) return;
            pageNumber = Math.Max(1u, Math.Min(pageNumber, tab.PdfDocument.PageCount));
            tab.PdfCurrentPage = pageNumber;
            PdfPageBox.Text = pageNumber.ToString();
            if (_pdfPageVisuals.TryGetValue(pageNumber - 1, out PdfPageVisual visual))
            {
                try
                {
                    var point = visual.Host.TransformToVisual(PdfPagesPanel).TransformPoint(new Point(0, 0));
                    PdfScrollViewer.ChangeView(null, point.Y, null);
                }
                catch { }
            }
            _ = RenderVisiblePdfPagesAsync();
        }

        private void PdfScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (_currentTab?.IsPdfView != true) return;
            _currentTab.PdfVerticalOffset = PdfScrollViewer.VerticalOffset;
            _ = RenderVisiblePdfPagesAsync();
        }

        private async void PdfSurface_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var tab = _currentTab;
            if (_pdfSizeUpdating || tab?.IsPdfView != true || tab.PdfDocument == null || !tab.PdfFitWidth) return;
            _pdfSizeUpdating = true;
            await Task.Delay(120);
            if (tab == _currentTab && tab.PdfFitWidth)
            {
                var fit = CalculatePdfFitZoom(tab);
                if (Math.Abs(fit - tab.PdfZoom) > 0.015) SetPdfZoom(tab, fit, true);
            }
            _pdfSizeUpdating = false;
        }

        private void PdfPreviousPage_Click(object sender, RoutedEventArgs e) => GoToPdfPage(_currentTab, Math.Max(1u, (_currentTab?.PdfCurrentPage ?? 1) - 1));
        private void PdfNextPage_Click(object sender, RoutedEventArgs e) => GoToPdfPage(_currentTab, (_currentTab?.PdfCurrentPage ?? 1) + 1);
        private void PdfPageBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            if (uint.TryParse(PdfPageBox.Text, out uint page)) GoToPdfPage(_currentTab, page);
            else UpdatePdfToolbar(_currentTab);
        }
        private void PdfZoomOut_Click(object sender, RoutedEventArgs e) { if (_currentTab?.IsPdfView == true) SetPdfZoom(_currentTab, _currentTab.PdfZoom - 0.1, false); }
        private void PdfZoomIn_Click(object sender, RoutedEventArgs e) { if (_currentTab?.IsPdfView == true) SetPdfZoom(_currentTab, _currentTab.PdfZoom + 0.1, false); }
        private void PdfActualSize_Click(object sender, RoutedEventArgs e) { if (_currentTab?.IsPdfView == true) SetPdfZoom(_currentTab, 1, false); }
        private void PdfFit_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTab?.IsPdfView != true) return;
            if (_currentTab.PdfFitWidth) SetPdfZoom(_currentTab, 1, false);
            else SetPdfZoom(_currentTab, CalculatePdfFitZoom(_currentTab), true);
        }
        private void PdfRotate_Click(object sender, RoutedEventArgs e) => RotatePdf(90);
        private void RotatePdf(int amount)
        {
            var tab = _currentTab;
            if (tab?.IsPdfView != true || tab.PdfDocument == null) return;
            tab.PdfRotation = ((tab.PdfRotation + amount) % 360 + 360) % 360;
            if (tab.PdfFitWidth) tab.PdfZoom = CalculatePdfFitZoom(tab);
            tab.PdfVerticalOffset = 0;
            BuildPdfPages(tab);
            UpdatePdfToolbar(tab);
        }

        private async void PdfSave_Click(object sender, RoutedEventArgs e)
        {
            var tab = _currentTab;
            if (tab?.PdfFile == null) return;
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.Downloads, SuggestedFileName = Path.GetFileNameWithoutExtension(tab.PdfFile.Name) };
            picker.FileTypeChoices.Add("PDF document", new List<string> { ".pdf" });
            var destination = await picker.PickSaveFileAsync();
            if (destination == null) return;
            try
            {
                await tab.PdfFile.CopyAndReplaceAsync(destination);
                RecordDownload(destination, tab.PdfSourceAddress);
                ShowTransientStatus("PDF saved.");
            }
            catch { ShowTransientStatus("The PDF could not be saved."); }
        }

        private async void PdfPrint_Click(object sender, RoutedEventArgs e)
        {
            var lifecycleGeneration = _pageLifecycleGeneration;
            var tab = _currentTab;
            var file = tab?.PdfFile;
            if (file == null || !IsPageOperationLive(lifecycleGeneration)) return;
            var launched = await Launcher.LaunchFileAsync(file);
            if (!IsPageOperationLive(lifecycleGeneration) || tab != _currentTab || tab.PdfFile != file) return;
            if (launched) ShowTransientStatus("Opened the PDF in its default app for printing.");
            else ShowTransientStatus("No installed app can print this PDF.");
        }

        private async void WebNote_Click(object sender, RoutedEventArgs e)
        {
            await DismissActiveMoreSubmenuAsync();
            var tab = _currentTab;
            var view = tab?.View;
            if (tab == null || view == null || NoteSurface.Visibility == Visibility.Visible) return;
            var viewGeneration = tab.ViewGeneration;
            StopReadAloud();
            try
            {
                BitmapImage image;
                if (tab.IsPdfView)
                {
                    image = await CaptureElementImageAsync(PdfSurface);
                    if (tab != _currentTab || !IsLiveWebView(tab, view, viewGeneration)) return;
                    PdfSurface.Visibility = Visibility.Collapsed;
                    PdfBar.Visibility = Visibility.Collapsed;
                }
                else
                {
                    if (!BrowserHost.Children.Contains(view) || !TryBeginWebViewCapture(view)) return;
                    try
                    {
                        using (var stream = new InMemoryRandomAccessStream())
                        {
                            await view.CapturePreviewToStreamAsync(stream);
                            if (tab != _currentTab || !IsLiveWebView(tab, view, viewGeneration) || !BrowserHost.Children.Contains(view)) return;
                            stream.Seek(0);
                            image = new BitmapImage();
                            await image.SetSourceAsync(stream);
                            if (tab != _currentTab || !IsLiveWebView(tab, view, viewGeneration) || !BrowserHost.Children.Contains(view)) return;
                        }
                    }
                    finally { EndWebViewCapture(view); }
                }
                if (image == null) throw new InvalidOperationException();
                NotePreview.Source = image;
                NoteCanvas.InkPresenter.StrokeContainer.Clear();
                view.Visibility = Visibility.Collapsed;
                NoteSurface.Visibility = Visibility.Visible;
                NoteBar.Visibility = Visibility.Visible;
                ShowTransientStatus("Draw on the captured page, then save your Web Note as a PNG.");
            }
            catch { ShowTransientStatus("A Web Note could not be created for this page."); }
        }

        private async void SaveNote_Click(object sender, RoutedEventArgs e)
        {
            if (NoteSurface.Visibility != Visibility.Visible) return;
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, SuggestedFileName = SafeFileName(_currentTab?.Title ?? "Web Note") };
            picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            try
            {
                var bitmap = new RenderTargetBitmap();
                await bitmap.RenderAsync(NoteSurface);
                var buffer = await bitmap.GetPixelsAsync();
                var pixels = new byte[(int)buffer.Length];
                using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
                using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
                {
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
                    await encoder.FlushAsync();
                }
                RecordDownload(file, _currentTab?.Address);
                ShowTransientStatus("Web Note saved.");
            }
            catch { ShowTransientStatus("The Web Note could not be saved."); }
        }

        private void ExitNote_Click(object sender, RoutedEventArgs e) => ExitNoteMode();

        private void ExitNoteMode()
        {
            NoteBar.Visibility = Visibility.Collapsed;
            NoteSurface.Visibility = Visibility.Collapsed;
            NotePreview.Source = null;
            NoteCanvas.InkPresenter.StrokeContainer.Clear();
            if (_currentTab?.IsPdfView == true)
            {
                ShowPdfTab(_currentTab);
            }
            else if (_currentTab?.View != null)
            {
                if (!BrowserHost.Children.Contains(_currentTab.View)) BrowserHost.Children.Add(_currentTab.View);
                _currentTab.View.Visibility = Visibility.Visible;
            }
        }

        private async void Share_Click(object sender, RoutedEventArgs e)
        {
            await DismissActiveMoreSubmenuAsync();
            if (_currentTab != null) DataTransferManager.ShowShareUI();
        }

        private void ShareManager_DataRequested(DataTransferManager sender, DataRequestedEventArgs args)
        {
            if (_currentTab == null) return;
            args.Request.Data.Properties.Title = _currentTab.Title ?? "Shared from Legacy Edge";
            args.Request.Data.Properties.Description = _currentTab.Address;
            if (_currentTab.LocalFile != null) args.Request.Data.SetStorageItems(new[] { _currentTab.LocalFile });
            else if (Uri.TryCreate(_currentTab.Address, UriKind.Absolute, out Uri uri)) args.Request.Data.SetWebLink(uri);
            else args.Request.Data.SetText(_currentTab.Address ?? string.Empty);
        }

        private async void SavePage_Click(object sender, RoutedEventArgs e)
        {
            await DismissActiveMoreSubmenuAsync();
            if (_currentTab?.IsPdfView == true)
            {
                PdfSave_Click(sender, e);
                return;
            }
            if (_currentTab?.LocalFile != null)
            {
                await SaveLocalFileCopyAsync(_currentTab);
                return;
            }
            if (_currentTab == null || !Uri.TryCreate(_currentTab.Address, UriKind.Absolute, out Uri uri)) return;
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.Downloads, SuggestedFileName = SafeFileName(_currentTab.Title ?? uri.Host) };
            picker.FileTypeChoices.Add("Web page", new List<string> { ".html" });
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;
            try
            {
                using (var client = new HttpClient())
                {
                    var html = await client.GetStringAsync(uri);
                    await FileIO.WriteTextAsync(file, html);
                }
                RecordDownload(file, uri.ToString());
                ShowTransientStatus("Page saved.");
            }
            catch { ShowTransientStatus("The page could not be saved."); }
        }

        private async void ViewSource_Click(object sender, RoutedEventArgs e)
        {
            var lifecycleGeneration = _pageLifecycleGeneration;
            var selectedTab = _currentTab;
            await DismissActiveMoreSubmenuAsync();
            if (!IsPageOperationLive(lifecycleGeneration) || selectedTab == null ||
                selectedTab != _currentTab || !_tabs.Contains(selectedTab)) return;
            if (selectedTab.IsPdfView)
            {
                ShowTransientStatus("PDF documents do not have HTML source.");
                return;
            }
            if (selectedTab.LocalFile != null)
            {
                await ViewLocalSourceAsync(selectedTab.LocalFile);
                return;
            }
            var sourceTab = selectedTab;
            var address = sourceTab.Address ?? string.Empty;
            if (address.StartsWith("view-source:", StringComparison.OrdinalIgnoreCase))
            {
                address = address.Substring("view-source:".Length);
                sourceTab = null;
            }
            await ViewSourceAsync(address, sourceTab);
        }

        private async Task SaveLocalFileCopyAsync(BrowserTab tab)
        {
            if (tab?.LocalFile == null) return;
            var extension = Path.GetExtension(tab.LocalFile.Name);
            if (string.IsNullOrWhiteSpace(extension)) extension = ".txt";
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                SuggestedFileName = Path.GetFileNameWithoutExtension(tab.LocalFile.Name)
            };
            picker.FileTypeChoices.Add("Local document", new List<string> { extension });
            var destination = await picker.PickSaveFileAsync();
            if (destination == null) return;
            try
            {
                await tab.LocalFile.CopyAndReplaceAsync(destination);
                RecordDownload(destination, tab.LocalFile.Path);
                ShowTransientStatus("File saved.");
            }
            catch { ShowTransientStatus("The file could not be saved."); }
        }

        private async Task ViewLocalSourceAsync(StorageFile file)
        {
            var lifecycleGeneration = _pageLifecycleGeneration;
            try
            {
                var source = await FileIO.ReadTextAsync(file);
                if (!IsPageOperationLive(lifecycleGeneration)) return;
                OpenSourceDocument(file.Path, file.Name, source, "Local file");
            }
            catch
            {
                if (IsPageOperationLive(lifecycleGeneration))
                    ShowTransientStatus("The local source could not be read.");
            }
        }

        private async Task ViewSourceAsync(string address, BrowserTab preferredTab = null, BrowserTab destinationTab = null)
        {
            var lifecycleGeneration = _pageLifecycleGeneration;
            if (!IsPageOperationLive(lifecycleGeneration)) return;
            if (!Uri.TryCreate(address, UriKind.Absolute, out Uri uri) ||
                (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                ShowTransientStatus("View source is available for HTTP and HTTPS pages.");
                return;
            }

            string source = null;
            var sourceKind = "Network response";
            var sourceView = preferredTab?.View;
            var sourceViewGeneration = preferredTab?.ViewGeneration ?? -1;
            var sourceNavigationGeneration = preferredTab?.NavigationGeneration ?? -1;
            var destinationViewGeneration = destinationTab?.ViewGeneration ?? -1;
            var destinationNavigationGeneration = destinationTab?.NavigationGeneration ?? -1;
            if (IsSameHttpDocument(preferredTab?.Address, uri) && sourceView != null &&
                preferredTab.LastNavigationSucceeded && !preferredTab.IsLoading &&
                BrowserHost.Children.Contains(sourceView) && TryBeginWebViewCapture(sourceView))
            {
                try
                {
                    const string script = "(function(){try{var d=document.doctype,r=document.documentElement,c,a,i,m,p;if(!r)return '';c=r.cloneNode(true);a=c.querySelectorAll('span[data-legacy-edge-find=\"1\"]');for(i=a.length-1;i>=0;i--){m=a[i];p=m.parentNode;if(p)p.replaceChild(document.createTextNode(m.textContent||''),m);}return(d?'<!DOCTYPE '+d.name+'>\\n':'')+c.outerHTML;}catch(e){return '';}})()";
                    source = await sourceView.InvokeScriptAsync("eval", new[] { script });
                    if (!IsPageOperationLive(lifecycleGeneration) ||
                        !IsLiveWebView(preferredTab, sourceView, sourceViewGeneration) ||
                        preferredTab.NavigationGeneration != sourceNavigationGeneration ||
                        preferredTab.IsLoading || !preferredTab.LastNavigationSucceeded ||
                        !IsSameHttpDocument(preferredTab.Address, uri)) source = null;
                    else if (!string.IsNullOrWhiteSpace(source)) sourceKind = "Loaded page DOM";
                }
                catch { source = null; }
                finally { EndWebViewCapture(sourceView); }
            }

            if (!IsPageOperationLive(lifecycleGeneration) ||
                !IsSourceDestinationLive(destinationTab, destinationViewGeneration, destinationNavigationGeneration)) return;

            if (string.IsNullOrWhiteSpace(source))
            {
                try
                {
                    using (var client = new HttpClient()) source = await client.GetStringAsync(uri);
                }
                catch { source = null; }
            }

            if (!IsPageOperationLive(lifecycleGeneration) ||
                !IsSourceDestinationLive(destinationTab, destinationViewGeneration, destinationNavigationGeneration)) return;

            if (string.IsNullOrWhiteSpace(source))
            {
                ShowTransientStatus("The page source could not be retrieved. The site may require a signed-in session.");
                return;
            }

            try { OpenSourceDocument(uri.ToString(), uri.Host, source, sourceKind, destinationTab); }
            catch
            {
                if (IsPageOperationLive(lifecycleGeneration))
                    ShowTransientStatus("The page source was retrieved but could not be displayed.");
            }
        }

        private bool IsSourceDestinationLive(BrowserTab destinationTab, int viewGeneration, int navigationGeneration)
        {
            return destinationTab == null || (destinationTab.View != null && _tabs.Contains(destinationTab) &&
                destinationTab.ViewGeneration == viewGeneration &&
                destinationTab.NavigationGeneration == navigationGeneration);
        }

        private static bool IsSameHttpDocument(string address, Uri requested)
        {
            if (requested == null || !Uri.TryCreate(address, UriKind.Absolute, out Uri current)) return false;
            var currentAddress = current.GetLeftPart(UriPartial.Path) + current.Query;
            var requestedAddress = requested.GetLeftPart(UriPartial.Path) + requested.Query;
            return string.Equals(currentAddress, requestedAddress, StringComparison.OrdinalIgnoreCase);
        }

        private void OpenSourceDocument(string address, string displayName, string source, string sourceKind, BrowserTab destinationTab = null)
        {
            if (!_initialized || _pageUnloadPending) throw new InvalidOperationException("The browser page is no longer active.");
            const int maximumSourceCharacters = 4 * 1024 * 1024;
            var truncated = source != null && source.Length > maximumSourceCharacters;
            if (truncated) source = source.Substring(0, maximumSourceCharacters);

            var title = "Source: " + (string.IsNullOrWhiteSpace(displayName) ? HostLabel(address) : displayName);
            var html = BuildSourceViewerHtml(title, address, source, sourceKind, truncated);
            var createdTab = destinationTab == null;
            var tab = destinationTab ?? CreateTab(null, true, false, false);
            if (!_tabs.Contains(tab) || tab.View == null) throw new InvalidOperationException("The source tab is no longer available.");
            var previousAddress = tab.Address;
            var previousTitle = tab.Title;
            var previousPendingAddress = tab.PendingAddress;
            var hadPreviousSourceAddress = _sourceDocumentAddresses.TryGetValue(tab.View, out string previousSourceAddress);
            var sourceAddress = "view-source:" + address;
            try
            {
                if (tab != _currentTab && !SwitchTab(tab)) throw new InvalidOperationException("The source tab could not be activated.");
                var localUri = tab.View.BuildLocalStreamUri("LegacyEdgeSource", "/source.html");
                _sourceDocumentAddresses[tab.View] = sourceAddress;
                tab.View.NavigateToLocalStreamUri(localUri, new HtmlStreamUriResolver(html));
            }
            catch
            {
                if (createdTab && _tabs.Contains(tab)) CloseTab(tab, false);
                else
                {
                    tab.Address = previousAddress;
                    tab.Title = previousTitle;
                    tab.PendingAddress = previousPendingAddress;
                    if (hadPreviousSourceAddress) _sourceDocumentAddresses[tab.View] = previousSourceAddress;
                    else _sourceDocumentAddresses.Remove(tab.View);
                    UpdateChrome();
                    RebuildTabs();
                }
                throw;
            }

            // The local-stream navigation is committed once EdgeHTML accepts it.
            // From here on, keep its mapping even if later chrome cleanup fails.
            try
            {
                ClearPdfState(tab);
                ClearLocalFileState(tab);
            }
            finally
            {
                tab.PendingAddress = null;
                tab.Address = sourceAddress;
                tab.Title = title;
            }
            UpdateChrome();
            RebuildTabs();
        }

        private static string BuildSourceViewerHtml(string title, string address, string source, string sourceKind, bool truncated)
        {
            var notice = truncated ? " &middot; Source was truncated after 4 million characters" : string.Empty;
            return "<!doctype html><html><head><meta charset='utf-8'><title>" + HtmlEncode(title) +
                "</title><style>html,body{margin:0;min-height:100%;background:#fff;color:#202020}" +
                "body{font-family:Consolas,'Courier New',monospace;font-size:13px}" +
                "header{position:sticky;top:0;z-index:1;padding:10px 16px;background:#f3f3f3;border-bottom:1px solid #d2d2d2;font-family:'Segoe UI',sans-serif}" +
                "header strong{display:block;font-size:14px;font-weight:600}header span{display:block;margin-top:2px;color:#555;font-size:12px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}" +
                "pre{box-sizing:border-box;margin:0;padding:16px;white-space:pre-wrap;word-wrap:break-word;tab-size:4;line-height:1.45}</style></head><body><header><strong>" +
                HtmlEncode(title) + "</strong><span>" + HtmlEncode(address) + " &middot; " + HtmlEncode(sourceKind) + notice +
                "</span></header><pre>" + HtmlEncode(source) + "</pre></body></html>";
        }

        private void RegisterForPrinting()
        {
            if (_printRegistered) return;
            try
            {
                if (!PrintManager.IsSupported()) return;
                _printDocument = new PrintDocument();
                _printDocumentSource = _printDocument.DocumentSource;
                _printDocument.Paginate += PrintDocument_Paginate;
                _printDocument.GetPreviewPage += PrintDocument_GetPreviewPage;
                _printDocument.AddPages += PrintDocument_AddPages;
                _printManager = PrintManager.GetForCurrentView();
                _printManager.PrintTaskRequested += PrintManager_PrintTaskRequested;
                _printRegistered = true;
            }
            catch
            {
                UnregisterForPrinting();
            }
        }

        private void UnregisterForPrinting()
        {
            ++_printOperationGeneration;
            if (_printManager != null)
            {
                try { _printManager.PrintTaskRequested -= PrintManager_PrintTaskRequested; }
                catch { }
            }
            if (_printDocument != null)
            {
                _printDocument.Paginate -= PrintDocument_Paginate;
                _printDocument.GetPreviewPage -= PrintDocument_GetPreviewPage;
                _printDocument.AddPages -= PrintDocument_AddPages;
            }
            _printRegistered = false;
            _printPreparing = false;
            _printManager = null;
            _printDocument = null;
            _printDocumentSource = null;
            ClearPrintVisual();
        }

        private void PrintManager_PrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
        {
            if (_printDocumentSource == null) return;
            var lifecycleGeneration = _pageLifecycleGeneration;
            var printOperationGeneration = _printOperationGeneration;
            var documentSource = _printDocumentSource;
            var title = string.IsNullOrWhiteSpace(_printTitle) ? "Legacy Edge" : _printTitle;
            if (title.Length > 80) title = title.Substring(0, 80);
            var task = args.Request.CreatePrintTask(title, sourceArgs => sourceArgs.SetSource(documentSource));
            TypedEventHandler<PrintTask, PrintTaskCompletedEventArgs> completionHandler = null;
            completionHandler = async (completedTask, completedArgs) =>
            {
                completedTask.Completed -= completionHandler;
                try
                {
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                    {
                        if (IsPageOperationLive(lifecycleGeneration) &&
                            printOperationGeneration == _printOperationGeneration)
                            ClearPrintVisual();
                    });
                }
                catch { }
            };
            task.Completed += completionHandler;
        }

        private void PrintDocument_Paginate(object sender, PaginateEventArgs args)
        {
            if (_printDocument == null) return;
            BuildPrintPage(args.PrintTaskOptions.GetPageDescription(0));
            _printDocument.SetPreviewPageCount(1, PreviewPageCountType.Final);
        }

        private void PrintDocument_GetPreviewPage(object sender, GetPreviewPageEventArgs args)
        {
            if (_printDocument != null && _printPage != null && args.PageNumber == 1)
                _printDocument.SetPreviewPage(1, _printPage);
        }

        private void PrintDocument_AddPages(object sender, AddPagesEventArgs args)
        {
            if (_printDocument == null) return;
            if (_printPage != null) _printDocument.AddPage(_printPage);
            _printDocument.AddPagesComplete();
        }

        private void BuildPrintPage(PrintPageDescription description)
        {
            RemovePrintPageVisual();
            var pageWidth = Math.Max(1, description.PageSize.Width);
            var pageHeight = Math.Max(1, description.PageSize.Height);
            var imageable = description.ImageableRect;
            if (imageable.Width < 1 || imageable.Height < 1)
                imageable = new Rect(48, 48, Math.Max(1, pageWidth - 96), Math.Max(1, pageHeight - 96));

            _printPage = new Grid
            {
                Width = pageWidth,
                Height = pageHeight,
                Background = new SolidColorBrush(Colors.White)
            };

            var content = new Grid
            {
                Width = imageable.Width,
                Height = imageable.Height
            };
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = _printTitle ?? "Legacy Edge",
                Foreground = new SolidColorBrush(Colors.Black),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 15,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 12)
            };
            content.Children.Add(title);

            var preview = new Image
            {
                Source = _printPreviewBitmap,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetRow(preview, 1);
            content.Children.Add(preview);

            var footer = new TextBlock
            {
                Text = "Current view  •  " + (_printAddress ?? string.Empty),
                Foreground = new SolidColorBrush(Color.FromArgb(255, 80, 80, 80)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 12, 0, 0)
            };
            Grid.SetRow(footer, 2);
            content.Children.Add(footer);

            _printPage.Children.Add(content);
            content.Margin = new Thickness(imageable.X, imageable.Y,
                Math.Max(0, pageWidth - imageable.Right), Math.Max(0, pageHeight - imageable.Bottom));
            content.HorizontalAlignment = HorizontalAlignment.Left;
            content.VerticalAlignment = VerticalAlignment.Top;
            PrintCanvas.Children.Add(_printPage);
            _printPage.Measure(new Size(pageWidth, pageHeight));
            _printPage.Arrange(new Rect(0, 0, pageWidth, pageHeight));
            _printPage.UpdateLayout();
        }

        private void ClearPrintVisual()
        {
            RemovePrintPageVisual();
            _printPreviewBitmap = null;
        }

        private void RemovePrintPageVisual()
        {
            if (_printPage != null) PrintCanvas?.Children.Remove(_printPage);
            _printPage = null;
        }

        private async Task<bool> PreparePrintPreviewAsync(BrowserTab tab)
        {
            var lifecycleGeneration = _pageLifecycleGeneration;
            var view = tab?.View;
            if (tab == null || view == null || !IsLiveWebView(tab, view, tab.ViewGeneration) ||
                tab.IsLoading || !BrowserHost.Children.Contains(view)) return false;
            var viewGeneration = tab.ViewGeneration;
            var navigationGeneration = tab.NavigationGeneration;
            if (!TryBeginWebViewCapture(view)) return false;
            try
            {
                using (var stream = new InMemoryRandomAccessStream())
                {
                    await view.CapturePreviewToStreamAsync(stream);
                    if (!IsPageOperationLive(lifecycleGeneration) || tab != _currentTab ||
                        tab.NavigationGeneration != navigationGeneration || tab.IsLoading ||
                        !IsLiveWebView(tab, view, viewGeneration) ||
                        !BrowserHost.Children.Contains(view)) return false;
                    stream.Seek(0);
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    if (!IsPageOperationLive(lifecycleGeneration) || tab != _currentTab ||
                        tab.NavigationGeneration != navigationGeneration || tab.IsLoading ||
                        !IsLiveWebView(tab, view, viewGeneration)) return false;
                    _printPreviewBitmap = bitmap;
                    _printTitle = string.IsNullOrWhiteSpace(tab.Title) ? HostLabel(tab.Address) : tab.Title;
                    _printAddress = tab.Address ?? string.Empty;
                    return true;
                }
            }
            catch { return false; }
            finally { EndWebViewCapture(view); }
        }

        private async void Print_Click(object sender, RoutedEventArgs e)
        {
            var lifecycleGeneration = _pageLifecycleGeneration;
            await DismissActiveMoreSubmenuAsync();
            if (!IsPageOperationLive(lifecycleGeneration)) return;
#if DEBUG
            _ = WriteDebugPrintReportAsync("Print command started.");
#endif
            if (_currentTab == null) return;
            if (_currentTab.IsPdfView)
            {
                PdfPrint_Click(sender, e);
                return;
            }
            if (_printPreparing) return;
            var printOperationGeneration = ++_printOperationGeneration;
            _printPreparing = true;
            try
            {
                if (!_printRegistered) RegisterForPrinting();
                if (!_printRegistered)
                {
                    ShowTransientStatus("Printing is not available on this Windows device.");
#if DEBUG
                    _ = WriteDebugPrintReportAsync("PrintManager is not supported or could not be registered.");
#endif
                    return;
                }
                var tab = _currentTab;
                if (!await PreparePrintPreviewAsync(tab))
                {
                    if (!IsPageOperationLive(lifecycleGeneration)) return;
                    ShowTransientStatus("A printable preview could not be captured for this page.");
#if DEBUG
                    _ = WriteDebugPrintReportAsync("Print preview capture failed.");
#endif
                    return;
                }
                if (!IsPageOperationLive(lifecycleGeneration)) return;
                var printUiShown = await PrintManager.ShowPrintUIAsync();
#if DEBUG
                _ = WriteDebugPrintReportAsync("Print preview prepared. ShowPrintUIAsync returned " + printUiShown + ".");
#endif
                if (!printUiShown && IsPageOperationLive(lifecycleGeneration))
                    ShowTransientStatus("Windows could not open the print dialog.");
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(exception);
#if DEBUG
                _ = WriteDebugPrintReportAsync("Print failed: 0x" + exception.HResult.ToString("X8") + " " + exception.Message);
#endif
                if (IsPageOperationLive(lifecycleGeneration))
                    ShowTransientStatus("Windows could not open the print dialog for this page.");
            }
            finally
            {
                if (lifecycleGeneration == _pageLifecycleGeneration &&
                    printOperationGeneration == _printOperationGeneration)
                    _printPreparing = false;
            }
        }

#if DEBUG
        private static async Task WriteDebugPrintReportAsync(string message)
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync("print-ui-qa.txt", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, DateTimeOffset.Now.ToString("o") + Environment.NewLine + (message ?? string.Empty));
            }
            catch { }
        }
#endif

        private async void FullScreen_Click(object sender, RoutedEventArgs e)
        {
            await DismissActiveMoreSubmenuAsync();
            if (_isFullScreen) ExitFullScreen(); else EnterFullScreen();
        }

        private void EnterFullScreen()
        {
            var view = ApplicationView.GetForCurrentView();
            if (!view.IsFullScreenMode && !view.TryEnterFullScreenMode()) return;
            CloseOmniboxPopup(false, true);
            _isFullScreen = true;
            HideTabPreviews();
            SidePanePopup.IsOpen = false;
            LegacySupportPopup.IsOpen = false;
            LayoutRoot.RowDefinitions[0].Height = new GridLength(0);
            LayoutRoot.RowDefinitions[1].Height = new GridLength(0);
            LayoutRoot.RowDefinitions[2].Height = new GridLength(0);
            LayoutRoot.RowDefinitions[3].Height = new GridLength(0);
            LayoutRoot.RowDefinitions[4].Height = new GridLength(0);
        }

        private void ExitFullScreen()
        {
            var view = ApplicationView.GetForCurrentView();
            if (view.IsFullScreenMode) view.ExitFullScreenMode();
            _isFullScreen = false;
            LayoutRoot.RowDefinitions[0].Height = GridLength.Auto;
            LayoutRoot.RowDefinitions[1].Height = new GridLength(53);
            LayoutRoot.RowDefinitions[2].Height = GridLength.Auto;
            LayoutRoot.RowDefinitions[3].Height = GridLength.Auto;
            LayoutRoot.RowDefinitions[4].Height = GridLength.Auto;
            UpdateLegacySupportPopup();
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            MoreMenu.Hide();
            CloseOmniboxPopup(false, true);
            SetHubPinned(false);
            HubPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Visible;

            _loadingSettings = true;
            StartupModeBox.SelectedValue = BrowserDataStore.StartupMode;
            if (StartupModeBox.SelectedIndex < 0) StartupModeBox.SelectedValue = "PreviousPages";
            StartupPageBox.Text = BrowserDataStore.StartupPage;
            NewTabModeBox.SelectedValue = BrowserDataStore.NewTabMode;
            if (NewTabModeBox.SelectedIndex < 0) NewTabModeBox.SelectedValue = "TopSitesAndSuggestedContent";
            ThemeBox.SelectedValue = BrowserDataStore.AppTheme;
            if (ThemeBox.SelectedIndex < 0) ThemeBox.SelectedValue = "Light";
            HomePageBox.Text = BrowserDataStore.HomePage;
            SearchTemplateBox.Text = BrowserDataStore.SearchTemplate;
            SearchProviderBox.SelectedValue = SearchProviderFromTemplate(BrowserDataStore.SearchTemplate);
            FavoritesBarToggle.IsOn = BrowserDataStore.ShowFavoritesBar;
            ShowHomeButtonToggle.IsOn = BrowserDataStore.ShowHomeButton;
            SearchSuggestionsToggle.IsOn = BrowserDataStore.ShowSearchSuggestions;
            AskDownloadToggle.IsOn = BrowserDataStore.AskWhatToDoWithEachDownload;
            LegacySupportNoticeToggle.IsOn = BrowserDataStore.ShowLegacySupportNotice;
            ShowSettingsSection(_activeSettingsSection);
            UpdateSettingsDependentControls();
            _loadingSettings = false;

            PositionSidePane();
            SidePanePopup.IsOpen = true;
        }

        private void SettingsSection_Click(object sender, RoutedEventArgs e)
        {
            ShowSettingsSection((sender as FrameworkElement)?.Tag as string ?? "general");
        }

        private void SettingsCompactSectionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var section = SettingsCompactSectionBox.SelectedValue as string;
            if (!string.IsNullOrWhiteSpace(section)) ShowSettingsSection(section);
        }

        private void ShowSettingsSection(string section)
        {
            _activeSettingsSection = section;
            SettingsGeneralPanel.Visibility = section == "general" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPrivacyPanel.Visibility = section == "privacy" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPasswordsPanel.Visibility = section == "passwords" ? Visibility.Visible : Visibility.Collapsed;
            SettingsAdvancedPanel.Visibility = section == "advanced" ? Visibility.Visible : Visibility.Collapsed;
            SettingsGeneralIndicator.Visibility = section == "general" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPrivacyIndicator.Visibility = section == "privacy" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPasswordsIndicator.Visibility = section == "passwords" ? Visibility.Visible : Visibility.Collapsed;
            SettingsAdvancedIndicator.Visibility = section == "advanced" ? Visibility.Visible : Visibility.Collapsed;
            if (SettingsCompactSectionBox != null && !string.Equals(SettingsCompactSectionBox.SelectedValue as string, section, StringComparison.Ordinal))
                SettingsCompactSectionBox.SelectedValue = section;
        }

        private void StartupModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSettingsDependentControls();
            if (!_loadingSettings) SaveSettingsFromControls();
        }

        private void NewTabModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loadingSettings) SaveSettingsFromControls();
        }

        private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loadingSettings) SaveSettingsFromControls();
        }

        private void SearchProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var provider = SearchProviderBox.SelectedValue as string;
            if (!_loadingSettings && provider != "Custom") SearchTemplateBox.Text = SearchTemplateForProvider(provider);
            UpdateSettingsDependentControls();
            if (!_loadingSettings) SaveSettingsFromControls();
        }

        private void SettingsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loadingSettings) SaveSettingsFromControls();
        }

        private void SettingsTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!_loadingSettings) SaveSettingsFromControls();
        }

        private void SidePanePopup_Closed(object sender, object e)
        {
            if (SettingsPanel.Visibility == Visibility.Visible) SaveSettingsFromControls();
            SidePanePopup.IsLightDismissEnabled = !_hubPinned;
            UpdateSidePaneWidth();
            ApplyHubPinLayout();
        }

        private void UpdateSettingsDependentControls()
        {
            StartupSpecificPagePanel.Visibility = (StartupModeBox.SelectedValue as string) == "SpecificPage" ? Visibility.Visible : Visibility.Collapsed;
            CustomSearchProviderPanel.Visibility = (SearchProviderBox.SelectedValue as string) == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool SaveSettingsFromControls()
        {
            if (_loadingSettings) return true;
            var startupMode = StartupModeBox.SelectedValue as string ?? "PreviousPages";
            var provider = SearchProviderBox.SelectedValue as string ?? "Bing";
            var search = provider == "Custom" ? (SearchTemplateBox.Text ?? string.Empty).Trim() : SearchTemplateForProvider(provider);
            if (!search.Contains("{0}") || !Uri.TryCreate(string.Format(search, "test"), UriKind.Absolute, out Uri _))
            {
                _loadingSettings = true;
                SearchTemplateBox.Text = BrowserDataStore.SearchTemplate;
                SearchProviderBox.SelectedValue = SearchProviderFromTemplate(BrowserDataStore.SearchTemplate);
                UpdateSettingsDependentControls();
                _loadingSettings = false;
                ShowTransientStatus("The custom search URL must be an absolute address containing {0}.");
                return false;
            }

            BrowserDataStore.StartupMode = startupMode;
            BrowserDataStore.RestorePreviousSession = startupMode == "PreviousPages";
            BrowserDataStore.StartupPage = NormalizeAddress(StartupPageBox.Text);
            var previousNewTabMode = BrowserDataStore.NewTabMode;
            var previousTheme = BrowserDataStore.AppTheme;
            BrowserDataStore.NewTabMode = NewTabModeBox.SelectedValue as string ?? "TopSitesAndSuggestedContent";
            BrowserDataStore.AppTheme = ThemeBox.SelectedValue as string ?? "Light";
            BrowserDataStore.HomePage = NormalizeAddress(HomePageBox.Text);
            BrowserDataStore.SearchTemplate = search;
            BrowserDataStore.ShowFavoritesBar = FavoritesBarToggle.IsOn;
            BrowserDataStore.ShowHomeButton = ShowHomeButtonToggle.IsOn;
            BrowserDataStore.ShowSearchSuggestions = SearchSuggestionsToggle.IsOn;
            BrowserDataStore.AskWhatToDoWithEachDownload = AskDownloadToggle.IsOn;
            BrowserDataStore.ShowLegacySupportNotice = LegacySupportNoticeToggle.IsOn;
            FavoritesBar.Visibility = BrowserDataStore.ShowFavoritesBar ? Visibility.Visible : Visibility.Collapsed;
            ApplyHomeButtonVisibility();
            UpdateLegacySupportPopup();
            if (!string.Equals(previousTheme, BrowserDataStore.AppTheme, StringComparison.Ordinal))
                ApplyTheme(true);
            if (!string.Equals(previousNewTabMode, BrowserDataStore.NewTabMode, StringComparison.Ordinal) &&
                _currentTab != null && !_currentTab.IsPrivate && string.IsNullOrWhiteSpace(_currentTab.Address))
                ShowStartPage(_currentTab);
            return true;
        }

        private static string SearchProviderFromTemplate(string template)
        {
            var value = template ?? string.Empty;
            if (value.IndexOf("google.", StringComparison.OrdinalIgnoreCase) >= 0) return "Google";
            if (value.IndexOf("duckduckgo.com", StringComparison.OrdinalIgnoreCase) >= 0) return "DuckDuckGo";
            if (value.IndexOf("bing.com", StringComparison.OrdinalIgnoreCase) >= 0) return "Bing";
            return "Custom";
        }

        private static string SearchTemplateForProvider(string provider)
        {
            switch (provider)
            {
                case "Google": return "https://www.google.com/search?q={0}";
                case "DuckDuckGo": return "https://duckduckgo.com/?q={0}";
                default: return "https://www.bing.com/search?q={0}";
            }
        }

        private void ApplyHomeButtonVisibility()
        {
            UpdateAdaptiveChrome();
        }

        private void UpdateAdaptiveChrome(double? widthOverride = null)
        {
            if (HomeButton == null || ToolbarSpacerColumn == null) return;
            var windowWidth = widthOverride ?? (ActualWidth > 0 ? ActualWidth : Window.Current.Bounds.Width);
            var width = Math.Max(320, windowWidth - (widthOverride.HasValue ? 0 : GetDockedPaneWidth()));

            var showHome = BrowserDataStore.ShowHomeButton;
            HomeButton.Visibility = showHome ? Visibility.Visible : Visibility.Collapsed;
            HomeButtonColumn.Width = new GridLength(showHome ? 48 : 0);
            ToolbarSpacerColumn.Width = new GridLength(0);

            var showHub = width >= 640;
            HubButton.Visibility = showHub ? Visibility.Visible : Visibility.Collapsed;
            HubButtonColumn.Width = new GridLength(showHub ? 48 : 0);

            var showDirectActions = width >= 860;
            WebNoteButton.Visibility = showDirectActions ? Visibility.Visible : Visibility.Collapsed;
            ShareButton.Visibility = showDirectActions ? Visibility.Visible : Visibility.Collapsed;
            WebNoteButtonColumn.Width = new GridLength(showDirectActions ? 48 : 0);
            ShareButtonColumn.Width = new GridLength(showDirectActions ? 48 : 0);

            if (PdfExpandedControls != null)
            {
                var compactPdf = width < 900;
                PdfExpandedControls.Visibility = compactPdf ? Visibility.Collapsed : Visibility.Visible;
                PdfOverflowButton.Visibility = compactPdf ? Visibility.Visible : Visibility.Collapsed;
                PdfTitlePanel.Visibility = width >= 560 ? Visibility.Visible : Visibility.Collapsed;
            }

            if (ReadAloudLabelColumn != null)
            {
                var compactReadAloud = width < 720;
                var veryNarrowReadAloud = width < 520;
                ReadAloudLabelColumn.Width = new GridLength(veryNarrowReadAloud ? 0 : compactReadAloud ? 52 : 170);
                ReadAloudLabelHost.Visibility = veryNarrowReadAloud ? Visibility.Collapsed : Visibility.Visible;
                ReadAloudLabelText.Visibility = compactReadAloud ? Visibility.Collapsed : Visibility.Visible;
                ReadAloudLabelIcon.Margin = compactReadAloud ? new Thickness(0) : new Thickness(0, 0, 10, 0);

                var showPosition = width >= 620;
                ReadAloudPositionColumn.Width = new GridLength(1, GridUnitType.Star);
                ReadAloudPositionText.Visibility = showPosition ? Visibility.Visible : Visibility.Collapsed;

                ReadAloudVoiceColumn.Width = new GridLength(compactReadAloud ? 52 : 132);
                ReadAloudOptionsText.Visibility = compactReadAloud ? Visibility.Collapsed : Visibility.Visible;
                ReadAloudOptionsIcon.Margin = compactReadAloud ? new Thickness(0) : new Thickness(0, 0, 8, 0);
            }

            if (FindBarLayout != null) FindBarLayout.Width = Math.Max(320, Math.Min(500, width - 20));

            UpdateDownloadShelfLayout(width);
            UpdateLegacySupportLayout(width);
        }

        private async void OpenProxySettings_Click(object sender, RoutedEventArgs e)
        {
            if (!await Launcher.LaunchUriAsync(new Uri("ms-settings:network-proxy")))
                ShowTransientStatus("Windows proxy settings could not be opened.");
        }

        private async void ClearData_Click(object sender, RoutedEventArgs e)
        {
            var result = await ShowDialogAsync(new ContentDialog
            {
                Title = "Clear browsing data?",
                Content = "This removes local history, the previous session, EdgeHTML cache, cookies, IndexedDB data, and temporary files. Favorites and reading list items are kept.",
                PrimaryButtonText = "Clear",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            });
            if (result != ContentDialogResult.Primary) return;
            BrowserDataStore.ClearBrowsingData();
            _history.Clear();
            await WebView.ClearTemporaryWebDataAsync();
            SidePanePopup.IsOpen = false;
            ShowTransientStatus("Browsing data cleared.");
        }

        private async void ToggleHomeToolbar_Click(object sender, RoutedEventArgs e)
        {
            await ToggleHomeToolbarAsync();
        }

        private async Task ToggleHomeToolbarAsync()
        {
            await DismissActiveMoreSubmenuAsync();
            BrowserDataStore.ShowHomeButton = !BrowserDataStore.ShowHomeButton;
            ApplyHomeButtonVisibility();
            ShowTransientStatus(BrowserDataStore.ShowHomeButton ? "Home button shown on the toolbar." : "Home button hidden from the toolbar.");
        }

        private async void ToggleFavoritesBar_Click(object sender, RoutedEventArgs e)
        {
            await DismissActiveMoreSubmenuAsync();
            BrowserDataStore.ShowFavoritesBar = !BrowserDataStore.ShowFavoritesBar;
            FavoritesBar.Visibility = BrowserDataStore.ShowFavoritesBar ? Visibility.Visible : Visibility.Collapsed;
            ShowTransientStatus(BrowserDataStore.ShowFavoritesBar ? "Favorites bar shown." : "Favorites bar hidden.");
        }

        private async void EdgeHelp_Click(object sender, RoutedEventArgs e)
        {
            await DismissActiveMoreSubmenuAsync();
            if (!await Launcher.LaunchUriAsync(new Uri("https://support.microsoft.com/microsoft-edge")))
                ShowTransientStatus("Microsoft Edge help could not be opened.");
        }

        private async void EdgeFeedback_Click(object sender, RoutedEventArgs e)
        {
            await DismissActiveMoreSubmenuAsync();
            if (!await Launcher.LaunchUriAsync(new Uri("https://aka.ms/edgefeedback")))
                ShowTransientStatus("The feedback page could not be opened.");
        }

        private async void About_Click(object sender, RoutedEventArgs e)
        {
            await DismissActiveMoreSubmenuAsync();
            await ShowDialogAsync(new ContentDialog
            {
                Title = "Microsoft Edge",
                Content = "Version 1.0\n\nThis is an independent recreation of the Windows 10 Microsoft Edge experience using the classic system EdgeHTML WebView. It is not affiliated with Microsoft.",
                CloseButtonText = "Close"
            });
        }

        private async Task<bool> OpenNewWindowAsync(string address, bool isPrivate, StorageFile file = null)
        {
            CoreApplicationView newView = null;
            try
            {
                newView = CoreApplication.CreateNewView();
                var newViewId = 0;
                await newView.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    var frame = new Frame();
                    frame.Navigate(typeof(MainPage), new WindowLaunchOptions { Address = address, IsPrivate = isPrivate, File = file });
                    Window.Current.Content = frame;
                    Window.Current.Activate();
                    newViewId = ApplicationView.GetForCurrentView().Id;
                });

                if (newViewId == 0) return false;
                var shown = await ApplicationViewSwitcher.TryShowAsStandaloneAsync(newViewId);
                if (!shown)
                {
                    await newView.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => Window.Current.Close());
                }
                return shown;
            }
            catch
            {
                if (newView != null)
                {
                    try { await newView.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => Window.Current.Close()); }
                    catch { }
                }
                return false;
            }
        }

        private async void NewWindow_Click(object sender, RoutedEventArgs e)
        {
            MoreMenu.Hide();
            if (!await OpenNewWindowAsync(null, false))
                ShowTransientStatus("A new browser window could not be opened.");
        }

        private async void NewPrivateWindow_Click(object sender, RoutedEventArgs e)
        {
            MoreMenu.Hide();
            var result = await ShowDialogAsync(new ContentDialog
            {
                Title = "InPrivate browsing",
                Content = "This window will not be added to history or session restore. The embedded EdgeHTML engine shares its cookie store across app windows, so this recreation cannot provide the full profile isolation of Microsoft Edge.",
                PrimaryButtonText = "Open InPrivate window",
                CloseButtonText = "Cancel"
            });
            if (result == ContentDialogResult.Primary && !await OpenNewWindowAsync(null, true))
                ShowTransientStatus("A new InPrivate window could not be opened.");
        }

        private async Task DownloadUriAsync(Uri uri, bool useDefaultDestination = false)
        {
            if (uri == null || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFtp))
            {
                ShowTransientStatus("Only HTTP, HTTPS, and FTP downloads are supported.");
                return;
            }

            if (!useDefaultDestination && BrowserDataStore.AskWhatToDoWithEachDownload)
            {
                ShowDownloadPrompt(uri);
                return;
            }

            try
            {
                var file = await ChooseDownloadDestinationAsync(uri, false);
                if (file == null) return;
                var item = await DownloadCoordinator.Current.EnqueueAsync(uri, file);
                if (item == null)
                {
                    ShowTransientStatus("The download could not be started.");
                    return;
                }
                ApplyDownloadSnapshots(DownloadCoordinator.Current.GetSnapshots(), true);
            }
            catch (Exception exception)
            {
                ShowTransientStatus(string.IsNullOrWhiteSpace(exception.Message) ? "The download could not be started." : exception.Message);
            }
        }

        private void ShowDownloadPrompt(Uri uri)
        {
            if (uri == null) return;
            if (_pendingDownloadActionBusy || (_pendingDownloadUri != null && !_pendingDownloadUri.Equals(uri)))
            {
                ShowTransientStatus("Finish the current download choice before starting another download.");
                return;
            }
            _pendingDownloadUri = uri;
            _downloadShelfHidden = false;
            var fileName = SuggestedDownloadFileName(uri);
            var extension = Path.GetExtension(fileName);
            var run = string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(extension, ".msi", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(extension, ".msix", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(extension, ".appx", StringComparison.OrdinalIgnoreCase);

            ConfigureDownloadShelfActionButtons(true);
            DownloadShelfTitleText.Text = "What do you want to do with " + fileName + "?";
            DownloadShelfDetailText.Text = "From: " + (string.IsNullOrWhiteSpace(uri.Host) ? uri.ToString() : uri.Host);
            DownloadShelfPauseButton.Content = run ? "Run" : "Open";
            DownloadShelfPauseButton.Visibility = Visibility.Visible;
            DownloadShelfCancelButton.Content = "Cancel";
            DownloadShelfCancelButton.Visibility = Visibility.Visible;
            DownloadShelfOpenButton.Content = "Save";
            DownloadShelfOpenButton.Visibility = Visibility.Visible;
            DownloadShelfFolderButton.Visibility = Visibility.Visible;
            DownloadShelfViewButton.Visibility = Visibility.Collapsed;
            DownloadShelfProgress.Visibility = Visibility.Collapsed;
            DownloadShelf.Visibility = Visibility.Visible;
            UpdateBottomNotificationPositions();
            _ = PopulateDownloadPromptSizeAsync(uri, fileName);
        }

        private async Task PopulateDownloadPromptSizeAsync(Uri uri, string fileName)
        {
            if (uri == null || uri.Scheme == Uri.UriSchemeFtp) return;
            try
            {
                ulong? contentLength;
                using (var client = new HttpClient())
                // ResponseHeadersRead avoids buffering the file. GET is used
                // instead of HEAD because many download endpoints omit the
                // entity length or reject HEAD even though GET is supported.
                using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                using (var response = await client.SendRequestAsync(request, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!response.IsSuccessStatusCode || response.Content == null) return;
                    contentLength = response.Content.Headers.ContentLength;
                }

                if (!contentLength.HasValue || contentLength.Value == 0 ||
                    _pendingDownloadUri == null || !_pendingDownloadUri.Equals(uri)) return;
                DownloadShelfTitleText.Text = "What do you want to do with " + fileName + " (" +
                    FormatDownloadSize(contentLength.Value) + ")?";
            }
            catch { }
        }

        private static string FormatDownloadSize(ulong bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            var value = (double)bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return (unit == 0 ? value.ToString("0") : value.ToString(value >= 10 ? "0.0" : "0.00")) + " " + units[unit];
        }

        private async Task StartPendingDownloadAsync(bool openAfterDownload, bool saveAs)
        {
            var uri = _pendingDownloadUri;
            if (uri == null || _pendingDownloadActionBusy) return;
            _pendingDownloadActionBusy = true;
            DownloadShelfPauseButton.IsEnabled = false;
            DownloadShelfCancelButton.IsEnabled = false;
            DownloadShelfOpenButton.IsEnabled = false;
            DownloadShelfFolderButton.IsEnabled = false;
            try
            {
                StorageFile file;
                if (openAfterDownload)
                    file = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(SuggestedDownloadFileName(uri), CreationCollisionOption.GenerateUniqueName);
                else if (saveAs)
                    file = await ChooseDownloadDestinationAsync(uri, true);
                else
                    file = await DownloadCoordinator.Current.CreateDefaultDestinationAsync(SuggestedDownloadFileName(uri));
                if (file == null || _pendingDownloadUri == null || !_pendingDownloadUri.Equals(uri)) return;

                var item = await DownloadCoordinator.Current.EnqueueAsync(uri, file);
                if (item == null)
                {
                    ShowTransientStatus("The download could not be started.");
                    return;
                }
                _pendingDownloadUri = null;
                if (openAfterDownload) _openAfterDownloadId = item.Id;
                ApplyDownloadSnapshots(DownloadCoordinator.Current.GetSnapshots(), true);
            }
            catch (Exception exception)
            {
                ShowTransientStatus(string.IsNullOrWhiteSpace(exception.Message) ? "The download could not be started." : exception.Message);
            }
            finally
            {
                _pendingDownloadActionBusy = false;
                DownloadShelfPauseButton.IsEnabled = true;
                DownloadShelfCancelButton.IsEnabled = true;
                DownloadShelfOpenButton.IsEnabled = true;
                DownloadShelfFolderButton.IsEnabled = true;
            }
        }

        private void RecordDownload(StorageFile file, string source)
        {
            if (file == null) return;
            DownloadCoordinator.Current.RecordCompletedFile(file, source);
            ApplyDownloadSnapshots(DownloadCoordinator.Current.GetSnapshots(), true);
        }

        private async Task<StorageFile> ChooseDownloadDestinationAsync(Uri uri, bool usePicker)
        {
            var fileName = SuggestedDownloadFileName(uri);
            if (!usePicker)
                return await DownloadCoordinator.Current.CreateDefaultDestinationAsync(fileName);

            var extension = Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(extension) || extension.Length > 12) extension = ".bin";
            var suggestedName = Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrWhiteSpace(suggestedName)) suggestedName = "download";
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                SuggestedFileName = suggestedName
            };
            picker.FileTypeChoices.Add("Downloaded file", new List<string> { extension });
            return await picker.PickSaveFileAsync();
        }

        private static string SuggestedDownloadFileName(Uri uri)
        {
            string name;
            try { name = Uri.UnescapeDataString(Path.GetFileName(uri?.AbsolutePath ?? string.Empty)); }
            catch { name = Path.GetFileName(uri?.AbsolutePath ?? string.Empty); }
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..") name = "download.bin";
            name = SafeFileName(name);
            var extension = Path.GetExtension(name);
            if (string.IsNullOrWhiteSpace(extension) || extension.Length > 12)
            {
                var stem = Path.GetFileNameWithoutExtension(name);
                if (string.IsNullOrWhiteSpace(stem)) stem = "download";
                name = stem + ".bin";
            }
            return name;
        }

        private static DownloadItem DownloadItemFromSender(object sender)
        {
            var element = sender as FrameworkElement;
            return element?.Tag as DownloadItem ?? element?.DataContext as DownloadItem;
        }

        private void DownloadPause_Click(object sender, RoutedEventArgs e)
        {
            var item = DownloadItemFromSender(sender);
            if (item != null && !DownloadCoordinator.Current.Pause(item))
                ShowTransientStatus("This download could not be paused.");
        }

        private void DownloadResume_Click(object sender, RoutedEventArgs e)
        {
            var item = DownloadItemFromSender(sender);
            if (item != null && !DownloadCoordinator.Current.Resume(item))
                ShowTransientStatus("This download could not be resumed yet.");
        }

        private void DownloadCancel_Click(object sender, RoutedEventArgs e)
        {
            var item = DownloadItemFromSender(sender);
            if (item != null && !DownloadCoordinator.Current.Cancel(item))
                ShowTransientStatus("This download is no longer active.");
        }

        private async void DownloadRetry_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var item = DownloadItemFromSender(sender);
                if (item == null) return;
                var retried = await DownloadCoordinator.Current.RetryAsync(item);
                if (retried != null) return;

                if (!Uri.TryCreate(item.SourceUrl, UriKind.Absolute, out Uri source))
                {
                    ShowTransientStatus("The original download address is unavailable.");
                    return;
                }
                var replacement = await ChooseDownloadDestinationAsync(source, true);
                if (replacement == null) return;
                retried = await DownloadCoordinator.Current.RetryAsync(item, replacement);
                if (retried == null) ShowTransientStatus("The download could not be retried.");
            }
            catch (Exception exception)
            {
                ShowTransientStatus(string.IsNullOrWhiteSpace(exception.Message) ? "The download could not be retried." : exception.Message);
            }
        }

        private async void DownloadList_ItemClick(object sender, ItemClickEventArgs e)
            => await OpenDownloadedFileAsync(e.ClickedItem as DownloadItem);

        private async void DownloadOpen_Click(object sender, RoutedEventArgs e)
            => await OpenDownloadedFileAsync(DownloadItemFromSender(sender));

        private async Task OpenDownloadedFileAsync(DownloadItem item)
        {
            if (item == null) return;
            var file = await DownloadCoordinator.Current.ResolveFileAsync(item);
            if (file == null)
            {
                ShowTransientStatus("The downloaded file was moved or deleted.");
                return;
            }
            if (!await Launcher.LaunchFileAsync(file)) ShowTransientStatus("No installed app can open this file.");
        }

        private async void DownloadShowInFolder_Click(object sender, RoutedEventArgs e)
            => await ShowDownloadedFileInFolderAsync(DownloadItemFromSender(sender));

        private async Task ShowDownloadedFileInFolderAsync(DownloadItem item)
        {
            if (item == null) return;
            var file = await DownloadCoordinator.Current.ResolveFileAsync(item);
            if (file == null)
            {
                ShowTransientStatus("The downloaded file was moved or deleted.");
                return;
            }

            var options = new FolderLauncherOptions();
            options.ItemsToSelect.Add(file);
            var launched = false;
            try
            {
                var folderPath = Path.GetDirectoryName(file.Path);
                if (!string.IsNullOrWhiteSpace(folderPath)) launched = await Launcher.LaunchFolderPathAsync(folderPath, options);
                if (!launched)
                {
                    var parent = await file.GetParentAsync();
                    if (parent != null) launched = await Launcher.LaunchFolderAsync(parent, options);
                }
            }
            catch { }
            if (!launched) ShowTransientStatus("The file's folder could not be opened.");
        }

        private async void DownloadRemove_Click(object sender, RoutedEventArgs e)
        {
            var item = DownloadItemFromSender(sender);
            if (item != null && !await DownloadCoordinator.Current.RemoveAsync(item))
                ShowTransientStatus("Active downloads cannot be removed from the list.");
        }

        private void DownloadCopyLink_Click(object sender, RoutedEventArgs e)
        {
            var item = DownloadItemFromSender(sender);
            if (item == null || string.IsNullOrWhiteSpace(item.SourceUrl)) return;
            var package = new DataPackage();
            package.SetText(item.SourceUrl);
            Clipboard.SetContent(package);
            ShowTransientStatus("Download link copied.");
        }

        private void DownloadReportUnsafe_Click(object sender, RoutedEventArgs e)
        {
            var item = DownloadItemFromSender(sender);
            if (item != null && !string.IsNullOrWhiteSpace(item.SourceUrl))
            {
                var package = new DataPackage();
                package.SetText(item.SourceUrl);
                Clipboard.SetContent(package);
            }
            CreateTab("https://www.microsoft.com/en-us/wdsi/AppRepSubmission", true);
            if (!_hubPinned) SidePanePopup.IsOpen = false;
            ShowTransientStatus("The download address was copied for the Microsoft SmartScreen report.");
        }

        private async void DownloadOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = UserDataPaths.GetDefault().Downloads;
                if (!await Launcher.LaunchFolderPathAsync(path)) ShowTransientStatus("The Downloads folder could not be opened.");
            }
            catch { ShowTransientStatus("The Downloads folder could not be opened."); }
        }

        private async void DownloadShelfPause_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingDownloadUri != null)
            {
                await StartPendingDownloadAsync(true, false);
                return;
            }
            var item = DownloadCoordinator.Current.GetSnapshot(_downloadShelfItemId);
            if (item == null) return;
            if (item.CanResume) DownloadCoordinator.Current.Resume(item);
            else DownloadCoordinator.Current.Pause(item);
            await Task.CompletedTask;
        }

        private void DownloadShelfCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingDownloadUri != null)
            {
                _pendingDownloadUri = null;
                _downloadShelfHidden = true;
                DownloadShelf.Visibility = Visibility.Collapsed;
                UpdateBottomNotificationPositions();
                return;
            }
            var item = DownloadCoordinator.Current.GetSnapshot(_downloadShelfItemId);
            if (item != null) DownloadCoordinator.Current.Cancel(item);
        }

        private async void DownloadShelfOpen_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingDownloadUri != null)
            {
                await StartPendingDownloadAsync(false, false);
                return;
            }
            var item = DownloadCoordinator.Current.GetSnapshot(_downloadShelfItemId);
            if (item == null) return;
            try
            {
                if (item.CanRetry)
                {
                    if (await DownloadCoordinator.Current.RetryAsync(item) == null)
                        ShowTransientStatus("The download could not be retried.");
                }
                else
                {
                    await OpenDownloadedFileAsync(item);
                }
            }
            catch (Exception exception)
            {
                ShowTransientStatus(string.IsNullOrWhiteSpace(exception.Message) ? "The download action failed." : exception.Message);
            }
        }

        private async void DownloadShelfFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingDownloadUri != null)
            {
                await StartPendingDownloadAsync(false, true);
                return;
            }
            await ShowDownloadedFileInFolderAsync(DownloadCoordinator.Current.GetSnapshot(_downloadShelfItemId));
        }

        private void DownloadShelfView_Click(object sender, RoutedEventArgs e) => OpenHub("downloads");

        private void DownloadShelfClose_Click(object sender, RoutedEventArgs e)
        {
            _pendingDownloadUri = null;
            _downloadShelfHidden = true;
            DownloadShelf.Visibility = Visibility.Collapsed;
            UpdateBottomNotificationPositions();
        }

#if DEBUG
        // Protocol-driven test hooks are compiled out of Release. They let the
        // lifecycle harness exercise the broker without automating FileSavePicker.
        private bool TryHandleDebugDownloadActivation(string address)
        {
            const string downloadPrefix = "debug-download/";
            const string promptPrefix = "debug-download-prompt/";
            const string commandPrefix = "debug-download-command/";
            var value = (address ?? string.Empty).TrimStart('/');
            if (value.StartsWith(promptPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string decoded;
                try { decoded = Uri.UnescapeDataString(value.Substring(promptPrefix.Length)); }
                catch { return true; }
                if (Uri.TryCreate(decoded, UriKind.Absolute, out Uri promptSource))
                {
                    if (_tabs.Count == 0) CreateTab(null, true);
                    ShowDownloadPrompt(promptSource);
                }
                return true;
            }
            if (value.StartsWith(downloadPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string decoded;
                try { decoded = Uri.UnescapeDataString(value.Substring(downloadPrefix.Length)); }
                catch { return true; }
                if (!Uri.TryCreate(decoded, UriKind.Absolute, out Uri source)) return true;
                if (_tabs.Count == 0) CreateTab(null, true);
                _ = DownloadUriAsync(source, true);
                return true;
            }

            if (!value.StartsWith(commandPrefix, StringComparison.OrdinalIgnoreCase)) return false;
            if (_tabs.Count == 0) CreateTab(null, true);
            var command = value.Substring(commandPrefix.Length).Trim('/').ToLowerInvariant();
            var records = DownloadCoordinator.Current.GetSnapshots();
            var active = records.FirstOrDefault(item => item.IsActive);
            var terminal = records.FirstOrDefault(item => item.IsTerminal);
            switch (command)
            {
                case "pause": if (active != null) DownloadCoordinator.Current.Pause(active); break;
                case "resume": if (active != null) DownloadCoordinator.Current.Resume(active); break;
                case "cancel": if (active != null) DownloadCoordinator.Current.Cancel(active); break;
                case "retry": if (terminal != null) _ = DownloadCoordinator.Current.RetryAsync(terminal); break;
                case "clear": _ = DownloadCoordinator.Current.ClearTerminalAsync(); break;
                case "promptsave": if (_pendingDownloadUri != null) _ = StartPendingDownloadAsync(false, false); break;
                case "promptopen": if (_pendingDownloadUri != null) _ = StartPendingDownloadAsync(true, false); break;
                case "promptcancel":
                    _pendingDownloadUri = null;
                    _downloadShelfHidden = true;
                    DownloadShelf.Visibility = Visibility.Collapsed;
                    break;
                case "openhub": OpenHub("downloads"); break;
                case "capturehub": OpenHub("downloads"); _ = CaptureDebugElementAsync(SidePaneBorder, "download-hub-qa.png"); break;
                case "captureshelf": _ = CaptureDebugElementAsync(DownloadShelf, "download-shelf-qa.png"); break;
                case "themelight":
                    BrowserDataStore.AppTheme = "Light";
                    ApplyTheme(true);
                    break;
                case "themedark":
                    BrowserDataStore.AppTheme = "Dark";
                    ApplyTheme(true);
                    break;
                case "capturestart":
                    _ = CaptureDebugWebViewAsync(_currentTab?.View, "start-page-qa.png");
                    break;
                case "capturechrome":
                    _ = CaptureDebugElementAsync(TitleBar, "title-bar-qa.png");
                    _ = CaptureDebugElementAsync(MainToolbar, "toolbar-qa.png");
                    break;
                case "capturefavorites":
                    OpenHub("favorites");
                    _ = CaptureDebugElementAsync(SidePaneBorder, "favorites-hub-qa.png");
                    break;
                case "capturesettings":
                    Settings_Click(this, new RoutedEventArgs());
                    _ = CaptureDebugElementAsync(SidePaneBorder, "settings-pane-qa.png");
                    break;
                case "capturesupport":
                    BrowserDataStore.ShowLegacySupportNotice = true;
                    UpdateLegacySupportPopup();
                    _ = CaptureDebugElementAsync(LegacySupportBorder, "support-notice-qa.png");
                    break;
                case "capturecompacthub":
                    OpenHub("books");
                    SetHubExpanded(false);
                    PositionSidePane();
                    _ = CaptureDebugElementAsync(SidePaneBorder, "compact-hub-qa.png");
                    break;
                case "capturenarrow":
                    _ = CaptureDebugNarrowLayoutsAsync();
                    break;
                case "stresschrome":
                    _ = RunDebugChromeStressAsync();
                    break;
                case "stressnestedhome":
                    _ = RunDebugNestedHomeStressAsync();
                    break;
                case "stresstaborder":
                    RunDebugTabOrderStress();
                    break;
                case "testfeatures":
                    _ = RunDebugFeatureQaAsync();
                    break;
                case "capturescrollbar":
                    _ = CaptureDebugLightScrollbarAsync();
                    break;
                case "capturewebscrollbar":
                    _ = CaptureDebugLightWebViewScrollbarAsync();
                    break;
                case "openprint":
                    Print_Click(this, new RoutedEventArgs());
                    break;
            }
            return true;
        }

        private async Task RunDebugFeatureQaAsync()
        {
            var failures = new List<string>();
            var acceleratorReady = KeyboardAccelerators.Any(item => item.Key == VirtualKey.F &&
                (item.Modifiers & VirtualKeyModifiers.Control) == VirtualKeyModifiers.Control);
            if (!acceleratorReady) failures.Add("Ctrl+F accelerator missing");

            BrowserTab testTab = null;
            var findCount = 0;
            var findCleanupReady = false;
            var sourceReady = false;
            var sourceReuseReady = false;
            var printReady = false;
            try
            {
                testTab = CreateTab(null, true, false, false);
                testTab.View.NavigateToString("<!doctype html><html><head><title>Feature QA</title></head><body><h1>Needle test</h1><p>needle alpha</p><p>beta NEEDLE gamma</p></body></html>");
                for (var attempt = 0; attempt < 40 && (testTab.IsLoading || !testTab.LastNavigationSucceeded); attempt++)
                    await Task.Delay(100);
                if (!testTab.LastNavigationSucceeded) failures.Add("test document did not load");

                testTab.Address = "https://debug.local/feature-qa";
                testTab.Title = "Feature QA";
                UpdateChrome();
                RebuildTabs();

                FindBar.Visibility = Visibility.Visible;
                FindBox.Text = string.Empty;
                await Task.Delay(60);
                FindBox.Text = "needle";
                await Task.Delay(300);
                if (_findInitializationTask != null) await _findInitializationTask;
                findCount = _findMatchCount;
                if (_findMatchCount != 3 || _findMatchIndex != 1)
                    failures.Add("find expected 1 of 3 but was " + _findMatchIndex + " of " + _findMatchCount);
                await FindOnPageAsync(false);
                if (_findMatchIndex != 2) failures.Add("find-next did not select match 2");
                await FindOnPageAsync(true);
                if (_findMatchIndex != 1) failures.Add("find-previous did not return to match 1");
                await CaptureDebugElementAsync(FindBar, "find-feature-qa.png");

                await ViewSourceAsync(testTab.Address, testTab);
                var sourceTab = _currentTab;
                for (var attempt = 0; attempt < 40 && sourceTab != null && (sourceTab.IsLoading || !sourceTab.LastNavigationSucceeded); attempt++)
                    await Task.Delay(100);
                if (sourceTab == null || ReferenceEquals(sourceTab, testTab) ||
                    !string.Equals(sourceTab.Address, "view-source:" + testTab.Address, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add("view source did not open a source tab");
                }
                else
                {
                    string sourceText = null;
                    var sourceView = sourceTab.View;
                    var sourceGeneration = sourceTab.ViewGeneration;
                    if (sourceView != null && TryBeginWebViewCapture(sourceView))
                    {
                        try
                        {
                            sourceText = await sourceView.InvokeScriptAsync("eval", new[] { "document.body ? document.body.innerText : ''" });
                            if (!IsLiveWebView(sourceTab, sourceView, sourceGeneration)) sourceText = null;
                        }
                        catch { sourceText = null; }
                        finally { EndWebViewCapture(sourceView); }
                    }
                    sourceReady = !string.IsNullOrWhiteSpace(sourceText) &&
                        sourceText.IndexOf("needle", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!sourceReady) failures.Add("view source content did not contain the loaded DOM");
                    await CaptureDebugWebViewAsync(sourceView, "view-source-feature-qa.png");
                }

                if (FindBar.Visibility == Visibility.Visible) CloseFind_Click(this, new RoutedEventArgs());
                if (_tabs.Contains(testTab)) SwitchTab(testTab);
                await Task.Delay(350);
                findCleanupReady = !_findHighlightedTabs.Contains(testTab) && testTab.View?.Visibility == Visibility.Visible;
                if (!findCleanupReady) failures.Add("background find highlights were not cleared before the tab was revealed");
                if (!_printRegistered) RegisterForPrinting();
                printReady = _printRegistered && await PreparePrintPreviewAsync(testTab) && _printPreviewBitmap != null;
                if (!printReady) failures.Add("print preview capture was not prepared");
                ClearPrintVisual();

                var tabCountBeforeSourceReuse = _tabs.Count;
                await ViewSourceAsync(testTab.Address, testTab, testTab);
                for (var attempt = 0; attempt < 40 && (testTab.IsLoading || !testTab.LastNavigationSucceeded); attempt++)
                    await Task.Delay(100);
                sourceReuseReady = _tabs.Count == tabCountBeforeSourceReuse && ReferenceEquals(_currentTab, testTab) &&
                    string.Equals(testTab.Address, "view-source:https://debug.local/feature-qa", StringComparison.OrdinalIgnoreCase);
                if (!sourceReuseReady) failures.Add("view-source navigation did not reuse its destination tab");
            }
            catch (Exception exception)
            {
                failures.Add(exception.GetType().Name + ": " + exception.Message);
            }

            var report = (failures.Count == 0 ? "PASS" : "FAIL") + Environment.NewLine +
                "Ctrl+F accelerator: " + acceleratorReady + Environment.NewLine +
                "Find matches: " + findCount + Environment.NewLine +
                "Find cleanup before reveal: " + findCleanupReady + Environment.NewLine +
                "View source DOM: " + sourceReady + Environment.NewLine +
                "View source reused destination: " + sourceReuseReady + Environment.NewLine +
                "Print preview: " + printReady + Environment.NewLine +
                (failures.Count == 0 ? "All feature checks passed." : string.Join(Environment.NewLine, failures));
            try
            {
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync("feature-qa.txt", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, report);
            }
            catch { }
            ShowTransientStatus(failures.Count == 0 ? "Find, View source, and Print QA passed." : "Feature QA failed: " + failures[0]);
        }

        private async Task CaptureDebugLightScrollbarAsync()
        {
            BrowserDataStore.AppTheme = "Light";
            ApplyTheme(true);
            var rows = new StackPanel { Background = new SolidColorBrush(Color.FromArgb(255, 250, 250, 250)) };
            for (var index = 1; index <= 24; index++)
            {
                rows.Children.Add(new TextBlock
                {
                    Text = "Scrollbar test row " + index,
                    Height = 34,
                    Padding = new Thickness(10, 7, 0, 0),
                    Foreground = new SolidColorBrush(Colors.Black)
                });
            }
            var viewer = new ScrollViewer
            {
                Width = 240,
                Height = 320,
                Content = rows,
                RequestedTheme = ElementTheme.Light,
                VerticalScrollMode = ScrollMode.Enabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            var host = new Border
            {
                Width = 240,
                Height = 320,
                Background = new SolidColorBrush(Colors.White),
                Child = viewer,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetRowSpan(host, 7);
            Canvas.SetZIndex(host, 100);
            LayoutRoot.Children.Add(host);
            try
            {
                await Task.Delay(250);
                viewer.ChangeView(null, 180, null, true);
                await CaptureDebugElementAsync(host, "scrollbar-light-qa.png");
            }
            finally
            {
                LayoutRoot.Children.Remove(host);
            }
        }

        private async Task CaptureDebugLightWebViewScrollbarAsync()
        {
            BrowserDataStore.AppTheme = "Light";
            ApplyTheme(true);
            var tab = CreateTab(null, true, false, false);
            tab.View.NavigateToString("<!doctype html><html><head><meta charset='utf-8'><title>Light scrollbar QA</title>" +
                "<style>html,body{margin:0;background:#fff;color:#111;font:16px 'Segoe UI',sans-serif}main{height:2600px;padding:24px;background:linear-gradient(#fff,#f4f4f4)}</style>" +
                "</head><body><main><h1>Light scrollbar test</h1><p>The document scrollbar should use the light browser theme.</p></main></body></html>");
            for (var attempt = 0; attempt < 40 && (tab.IsLoading || !tab.LastNavigationSucceeded); attempt++)
                await Task.Delay(100);
            await CaptureDebugWebViewAsync(tab.View, "webview-scrollbar-light-qa.png");
        }

        private void RunDebugTabOrderStress()
        {
            var failure = string.Empty;
            var originalOrder = _tabs.ToList();
            var originalCurrent = _currentTab;
            var originalAttached = BrowserHost.Children.OfType<WebView>().ToList();
            var probes = new List<BrowserTab>();
            try
            {
                for (var index = 0; index < 4; index++)
                {
                    var probe = CreateTab(null, false, false, false);
                    probe.Title = "Tab-order probe " + (char)('A' + index);
                    probes.Add(probe);
                }

                var pinnedA = probes[0];
                var pinnedB = probes[1];
                var regularA = probes[2];
                var regularB = probes[3];
                TogglePinnedTab(pinnedA);
                TogglePinnedTab(pinnedB);

                var firstProbePinnedIndex = _tabs.Count(tab => tab.IsPinned) - 2;
                CommitTabReorder(pinnedB, firstProbePinnedIndex);
                if (_tabs.IndexOf(pinnedB) != firstProbePinnedIndex)
                    failure = "A pinned tab did not move within the pinned partition.";
                if (string.IsNullOrWhiteSpace(failure))
                    failure = ValidateDebugTabOrderState(originalCurrent, originalAttached, probes);

                if (string.IsNullOrWhiteSpace(failure))
                {
                    CommitTabReorder(pinnedB, _tabs.Count - 1);
                    var pinnedCount = _tabs.Count(tab => tab.IsPinned);
                    if (_tabs.IndexOf(pinnedB) != pinnedCount - 1)
                        failure = "A pinned tab crossed into the regular partition.";
                }
                if (string.IsNullOrWhiteSpace(failure))
                    failure = ValidateDebugTabOrderState(originalCurrent, originalAttached, probes);

                if (string.IsNullOrWhiteSpace(failure))
                {
                    var regularTarget = _tabs.IndexOf(regularA);
                    CommitTabReorder(regularB, regularTarget);
                    if (_tabs.IndexOf(regularB) != regularTarget || _tabs.IndexOf(regularB) >= _tabs.IndexOf(regularA))
                        failure = "A regular tab did not move within the regular partition.";
                }
                if (string.IsNullOrWhiteSpace(failure))
                    failure = ValidateDebugTabOrderState(originalCurrent, originalAttached, probes);

                if (string.IsNullOrWhiteSpace(failure))
                {
                    CommitTabReorder(regularB, 0);
                    var pinnedCount = _tabs.Count(tab => tab.IsPinned);
                    if (_tabs.IndexOf(regularB) != pinnedCount)
                        failure = "A regular tab crossed into the pinned partition.";
                }
                if (string.IsNullOrWhiteSpace(failure))
                    failure = ValidateDebugTabOrderState(originalCurrent, originalAttached, probes);
            }
            catch (Exception exception)
            {
                failure = "Tab-order stress test threw " + exception.GetType().Name + ".";
            }
            finally
            {
                foreach (var probe in probes.Where(_tabs.Contains).ToList()) CloseTab(probe, false);
                if (originalCurrent != null && _tabs.Contains(originalCurrent) && !ReferenceEquals(_currentTab, originalCurrent))
                    SwitchTab(originalCurrent);
            }

            if (string.IsNullOrWhiteSpace(failure) && !_tabs.SequenceEqual(originalOrder))
                failure = "The original tab order was not restored after cleanup.";
            if (string.IsNullOrWhiteSpace(failure))
                failure = ValidateDebugTabOrderState(originalCurrent, originalAttached, new List<BrowserTab>());
            ShowTransientStatus(string.IsNullOrWhiteSpace(failure) ? "Tab-order stress test passed." : failure);
        }

        private string ValidateDebugTabOrderState(BrowserTab expectedCurrent, IList<WebView> expectedAttached, IList<BrowserTab> probes)
        {
            if (!ReferenceEquals(_currentTab, expectedCurrent)) return "Tab reordering changed the active tab.";

            var regularSeen = false;
            foreach (var tab in _tabs)
            {
                if (!tab.IsPinned) regularSeen = true;
                else if (regularSeen) return "Pinned and regular tab partitions became interleaved.";
            }

            var attached = BrowserHost.Children.OfType<WebView>().ToList();
            if (attached.Count != expectedAttached.Count || attached.Any(view => !expectedAttached.Contains(view)))
                return "Tab reordering changed the attached WebView set.";
            if (probes.Any(probe => probe.View != null && BrowserHost.Children.Contains(probe.View)))
                return "A temporary background tab attached its WebView.";
            return string.Empty;
        }

        private async Task RunDebugChromeStressAsync()
        {
            var failure = string.Empty;
            for (var index = 0; index < 12; index++)
            {
                BrowserDataStore.ShowHomeButton = index % 2 == 0;
                ApplyHomeButtonVisibility();
                OpenHub(index % 2 == 0 ? "favorites" : "history");
                SetHubExpanded(index % 3 != 0);
                PositionSidePane();
                SidePanePopup.IsOpen = false;

                var temporary = CreateTab(null, true);
                await Task.Delay(35);
                CloseTab(temporary, false);
                await Task.Delay(35);

                var attachedCount = BrowserHost.Children.OfType<WebView>().Count();
                if (attachedCount > 1)
                {
                    failure = "More than one WebView was attached (" + attachedCount + ").";
                    break;
                }
            }

            var pdfProbe = _currentTab;
            if (string.IsNullOrWhiteSpace(failure) && pdfProbe?.View != null)
            {
                pdfProbe.IsPdfView = true;
                ShowPdfTab(pdfProbe);
                await Task.Delay(35);
                if (BrowserHost.Children.OfType<WebView>().Any())
                    failure = "A WebView remained attached while the active tab was showing a PDF.";

                ClearPdfState(pdfProbe);
                await Task.Delay(35);
                var attachedViews = BrowserHost.Children.OfType<WebView>().ToList();
                if (string.IsNullOrWhiteSpace(failure) &&
                    (attachedViews.Count != 1 || !ReferenceEquals(attachedViews[0], pdfProbe.View)))
                    failure = "The active WebView was not restored after leaving a PDF.";
            }

            BrowserDataStore.ShowHomeButton = true;
            ApplyHomeButtonVisibility();
            OpenHub("favorites");
            SetHubExpanded(false);
            PositionSidePane();
            await CaptureDebugElementAsync(MainToolbar, "toolbar-fixed-qa.png");
            await CaptureDebugElementAsync(SidePaneBorder, "hub-fixed-qa.png");
            SidePanePopup.IsOpen = false;
            ShowTransientStatus(string.IsNullOrWhiteSpace(failure) ? "Chrome stress test passed." : failure);
        }

        private async Task RunDebugNestedHomeStressAsync()
        {
            for (var index = 0; index < 10; index++)
            {
                MoreMenu.ShowAt(MoreButton);
                await Task.Delay(70);
                ShowInToolbarFlyout.ShowAt(ShowInToolbarMenuButton);
                await Task.Delay(70);
                await ToggleHomeToolbarAsync();
                await Task.Delay(70);
            }

            BrowserDataStore.ShowHomeButton = true;
            ApplyHomeButtonVisibility();
            await CaptureDebugElementAsync(MainToolbar, "toolbar-nested-home-qa.png");
            ShowTransientStatus("Nested Home menu stress test passed.");
        }

        private async Task CaptureDebugNarrowLayoutsAsync()
        {
            ApplicationView.GetForCurrentView().TryResizeView(new Size(500, 450));
            await Task.Delay(650);

            SetHubExpanded(true);
            OpenHub("favorites");
            await CaptureDebugElementAsync(SidePaneBorder, "narrow-hub-qa.png");

            OpenHub("books");
            SetHubExpanded(false);
            PositionSidePane();
            await CaptureDebugElementAsync(SidePaneBorder, "narrow-compact-hub-qa.png");

            Settings_Click(this, new RoutedEventArgs());
            await CaptureDebugElementAsync(SidePaneBorder, "narrow-settings-qa.png");

            ShowDownloadPrompt(new Uri("https://example.com/test-file.zip"));
            await CaptureDebugElementAsync(DownloadShelf, "narrow-download-shelf-qa.png");
            BrowserDataStore.ShowLegacySupportNotice = true;
            UpdateLegacySupportPopup();
            await CaptureDebugElementAsync(LegacySupportBorder, "narrow-support-notice-qa.png");
        }

        private static async Task CaptureDebugElementAsync(FrameworkElement element, string fileName)
        {
            if (element == null) return;
            await Task.Delay(350);
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(element);
            var buffer = await bitmap.GetPixelsAsync();
            var pixels = new byte[(int)buffer.Length];
            using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
            using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
                await encoder.FlushAsync();
            }
        }

        private static async Task CaptureDebugWebViewAsync(WebView view, string fileName)
        {
            if (view == null) return;
            await Task.Delay(750);
            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
            using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                await view.CapturePreviewToStreamAsync(stream);
                await stream.FlushAsync();
            }
        }
#endif

        private void Dispatcher_AcceleratorKeyActivated(CoreDispatcher sender, AcceleratorKeyEventArgs args)
        {
            if (args.Handled) return;
            if (args.EventType != CoreAcceleratorKeyEventType.KeyDown && args.EventType != CoreAcceleratorKeyEventType.SystemKeyDown) return;
            var window = Window.Current.CoreWindow;
            var control = (window.GetKeyState(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
            var shift = (window.GetKeyState(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;
            var alt = (window.GetKeyState(VirtualKey.Menu) & CoreVirtualKeyStates.Down) != 0;
            var handled = true;

            if (control && shift && args.VirtualKey == VirtualKey.T)
            {
                if (_closedTabs.Count > 0) CreateTab(_closedTabs.Pop());
            }
            else if (control && shift && args.VirtualKey == VirtualKey.G) ReadAloud_Click(this, new RoutedEventArgs());
            else if (control && shift && args.VirtualKey == VirtualKey.R) ReadingView_Click(this, new RoutedEventArgs());
            else if (control && shift && args.VirtualKey == VirtualKey.P) NewPrivateWindow_Click(this, new RoutedEventArgs());
            else if (control && shift && args.VirtualKey == VirtualKey.Delete) ClearData_Click(this, new RoutedEventArgs());
            else if (control && args.VirtualKey == VirtualKey.T) NewTab_Click(this, new RoutedEventArgs());
            else if (control && args.VirtualKey == VirtualKey.N) NewWindow_Click(this, new RoutedEventArgs());
            else if (control && args.VirtualKey == VirtualKey.O) OpenFile_Click(this, new RoutedEventArgs());
            else if (control && (args.VirtualKey == VirtualKey.W || args.VirtualKey == VirtualKey.F4)) CloseTab(_currentTab);
            else if (control && (args.VirtualKey == VirtualKey.L || args.VirtualKey == VirtualKey.K || args.VirtualKey == VirtualKey.E)) FocusAddressBar();
            else if (control && args.VirtualKey == VirtualKey.R) Refresh_Click(this, new RoutedEventArgs());
            else if (control && args.VirtualKey == VirtualKey.F) Find_Click(this, new RoutedEventArgs());
            else if (control && args.VirtualKey == VirtualKey.D) Favorite_Click(this, new RoutedEventArgs());
            else if (control && args.VirtualKey == VirtualKey.H) OpenHub("history");
            else if (control && args.VirtualKey == VirtualKey.J) OpenHub("downloads");
            else if (control && args.VirtualKey == VirtualKey.P) Print_Click(this, new RoutedEventArgs());
            else if (control && args.VirtualKey == VirtualKey.S) SavePage_Click(this, new RoutedEventArgs());
            else if (control && _currentTab?.IsPdfView == true && (int)args.VirtualKey == 220) PdfFit_Click(this, new RoutedEventArgs());
            else if (control && _currentTab?.IsPdfView == true && (int)args.VirtualKey == 219) RotatePdf(-90);
            else if (control && _currentTab?.IsPdfView == true && (int)args.VirtualKey == 221) RotatePdf(90);
            else if (control && (args.VirtualKey == VirtualKey.Add || (int)args.VirtualKey == 187)) ChangeZoom(10);
            else if (control && (args.VirtualKey == VirtualKey.Subtract || (int)args.VirtualKey == 189)) ChangeZoom(-10);
            else if (control && args.VirtualKey == VirtualKey.Number0) ResetZoom_Click(this, new RoutedEventArgs());
            else if (control && (int)args.VirtualKey >= (int)VirtualKey.Number1 && (int)args.VirtualKey <= (int)VirtualKey.Number9) ActivateNumberedTab((int)args.VirtualKey - (int)VirtualKey.Number1);
            else if (control && args.VirtualKey == VirtualKey.Tab) CycleTab(shift ? -1 : 1);
            else if (_currentTab?.IsPdfView == true && args.VirtualKey == VirtualKey.PageUp) PdfPreviousPage_Click(this, new RoutedEventArgs());
            else if (_currentTab?.IsPdfView == true && args.VirtualKey == VirtualKey.PageDown) PdfNextPage_Click(this, new RoutedEventArgs());
            else if ((shift && args.VirtualKey == VirtualKey.F10) || args.VirtualKey == VirtualKey.Application) OpenKeyboardPageContextMenu();
            else if (alt && args.VirtualKey == VirtualKey.D) FocusAddressBar();
            else if (alt && args.VirtualKey == VirtualKey.Left) Back_Click(this, new RoutedEventArgs());
            else if (alt && args.VirtualKey == VirtualKey.Right) Forward_Click(this, new RoutedEventArgs());
            else if (alt && args.VirtualKey == VirtualKey.Home) Navigate(_currentTab, BrowserDataStore.HomePage);
            else if (alt && args.VirtualKey == VirtualKey.X) More_Click(this, new RoutedEventArgs());
            else if (args.VirtualKey == VirtualKey.F5) Refresh_Click(this, new RoutedEventArgs());
            else if (!alt && args.VirtualKey == VirtualKey.F4) FocusAddressBar();
            else if (args.VirtualKey == VirtualKey.F3)
            {
                if (FindBar.Visibility == Visibility.Visible && !string.IsNullOrEmpty(FindBox.Text))
                    _ = FindOnPageAsync(shift);
                else
                    OpenFindBar();
            }
            else if (args.VirtualKey == VirtualKey.F11) FullScreen_Click(this, new RoutedEventArgs());
            else if (args.VirtualKey == VirtualKey.Escape) HandleEscape();
            else handled = false;

            args.Handled = handled;
        }

        private void CycleTab(int direction)
        {
            if (_tabs.Count < 2 || _currentTab == null) return;
            var index = (_tabs.IndexOf(_currentTab) + direction + _tabs.Count) % _tabs.Count;
            SwitchTab(_tabs[index]);
        }

        private void ActivateNumberedTab(int index)
        {
            if (_tabs.Count == 0) return;
            if (index == 8) index = _tabs.Count - 1;
            if (index >= 0 && index < _tabs.Count) SwitchTab(_tabs[index]);
        }

        private void HandleEscape()
        {
            if (_tabDragPointer != null)
            {
                EndTabPointerGesture(false, true, true, true);
            }
            else if (OmniboxPopup.IsOpen)
            {
                DismissOmniboxSelectionPreview();
            }
            else if (_omniboxEditing)
            {
                CloseOmniboxPopup(true, true);
                FocusCurrentContent();
            }
            else if (ReadAloudBar.Visibility == Visibility.Visible) StopReadAloud();
            else if (NoteSurface.Visibility == Visibility.Visible) ExitNoteMode();
            else if (FindBar.Visibility == Visibility.Visible) CloseFind_Click(this, new RoutedEventArgs());
            else if (SidePanePopup.IsOpen) SidePanePopup.IsOpen = false;
            else if (TabPreviewBar.Visibility == Visibility.Visible) HideTabPreviews();
            else if (_isFullScreen) ExitFullScreen();
            else if (_currentTab?.IsLoading == true) _currentTab.View.Stop();
        }

        private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
        {
            if (_dialogOpen) return ContentDialogResult.None;
            _dialogOpen = true;
            try { return await dialog.ShowAsync(); }
            catch { return ContentDialogResult.None; }
            finally { _dialogOpen = false; }
        }

        private void ShowTransientStatus(string message)
        {
            StatusText.Text = message;
            StatusBubble.Visibility = Visibility.Visible;
            if (_statusTimer != null) _statusTimer.Stop();
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _statusTimer.Tick += (sender, args) => { _statusTimer.Stop(); StatusBubble.Visibility = Visibility.Collapsed; };
            _statusTimer.Start();
        }

        private static bool IsWebAddress(string value) => Uri.TryCreate(value, UriKind.Absolute, out Uri uri) && (uri.Scheme == "http" || uri.Scheme == "https");
        private static string HostLabel(string value) => Uri.TryCreate(value, UriKind.Absolute, out Uri uri) ? uri.Host : "New tab";
        private static string EscapeJavaScript(string value) => (value ?? "").Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "\\r").Replace("\n", "\\n").Replace("</", "<\\/");

        private static string SafeFileName(string value)
        {
            var cleaned = value ?? "download";
            foreach (var character in Path.GetInvalidFileNameChars()) cleaned = cleaned.Replace(character, '_');
            return cleaned.Length > 80 ? cleaned.Substring(0, 80) : cleaned;
        }

        private static string HtmlEncode(string value)
        {
            return (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
        }

        private static async Task<ReadingContent> ExtractReadingContentAsync(WebView view)
        {
            if (view == null) return null;
            const string script = "(function(){function clean(v){return (v||'').replace(/\\s+/g,' ').replace(/^\\s+|\\s+$/g,'');}var root=document.querySelector('article,[role=main],main')||document.body;if(!root)return '';var title=clean((root.querySelector('h1')||document.querySelector('h1')||{}).textContent)||clean(document.title);var by=document.querySelector('.byline,[rel=author],[itemprop=author]');var byline=by?clean(by.textContent):'';function skipped(n){for(var p=n;p&&p!==root;p=p.parentElement){var t=p.tagName;if(t==='NAV'||t==='ASIDE'||t==='FORM'||t==='FOOTER')return true;var c=typeof p.className==='string'?p.className:'';if(/(^|\\s)(nav|menu|advert|advertisement|sidebar|social|share|comments?)(\\s|$)/i.test(c))return true;}return false;}var nodes=root.querySelectorAll('h1,h2,h3,p,blockquote,li,img,figcaption');var blocks=[],seen={},total=0,index=0;for(var i=0;i<nodes.length&&blocks.length<300&&total<120000;i++){var n=nodes[i];if(skipped(n))continue;var style=window.getComputedStyle?window.getComputedStyle(n):null;if(style&&(style.display==='none'||style.visibility==='hidden'))continue;var tag=(n.tagName||'p').toLowerCase();if(tag==='img'){var src=n.src||'';if(src)blocks.push({kind:'img',text:'',url:src,alt:clean(n.alt),index:-1});continue;}var text=clean(n.textContent);if(!text||(text.length<20&&(tag==='p'||tag==='li'||tag==='blockquote'))||seen[text])continue;seen[text]=true;n.setAttribute('data-legacy-edge-speech-index',String(index));blocks.push({kind:tag,text:text,url:'',alt:'',index:index});total+=text.length;index++;}if(!blocks.length){var fallback=clean(root.innerText);if(fallback)blocks.push({kind:'p',text:fallback,url:'',alt:'',index:0});}return JSON.stringify({title:title,byline:byline,blocks:blocks});})()";

            try
            {
                var raw = await view.InvokeScriptAsync("eval", new[] { script });
                if (JsonObject.TryParse(raw, out JsonObject json))
                {
                    var content = new ReadingContent
                    {
                        Title = json.GetNamedString("title", view.DocumentTitle ?? string.Empty),
                        Byline = json.GetNamedString("byline", string.Empty)
                    };
                    var blocks = json.GetNamedArray("blocks", new JsonArray());
                    foreach (var value in blocks)
                    {
                        if (value.ValueType != JsonValueType.Object) continue;
                        var item = value.GetObject();
                        content.Blocks.Add(new ReadingBlock
                        {
                            Kind = item.GetNamedString("kind", "p"),
                            Text = item.GetNamedString("text", string.Empty),
                            Url = item.GetNamedString("url", string.Empty),
                            Alt = item.GetNamedString("alt", string.Empty),
                            SpeechIndex = (int)item.GetNamedNumber("index", -1)
                        });
                    }
                    if (content.Blocks.Any(block => !string.IsNullOrWhiteSpace(block.Text))) return content;
                }
            }
            catch { }

            try
            {
                var text = await view.InvokeScriptAsync("eval", new[] { "document.body ? document.body.innerText : ''" });
                if (string.IsNullOrWhiteSpace(text)) return null;
                var fallback = new ReadingContent { Title = view.DocumentTitle ?? string.Empty };
                var paragraphs = text.Split(new[] { "\r\n\r\n", "\n\n", "\r\r" }, StringSplitOptions.RemoveEmptyEntries);
                var speechIndex = 0;
                foreach (var paragraph in paragraphs.Take(300))
                {
                    var normalized = NormalizeReadingText(paragraph);
                    if (normalized.Length == 0) continue;
                    fallback.Blocks.Add(new ReadingBlock { Kind = "p", Text = normalized, SpeechIndex = speechIndex++ });
                }
                return fallback.Blocks.Count == 0 ? null : fallback;
            }
            catch { return null; }
        }

        private static string NormalizeReadingText(string value)
        {
            return string.Join(" ", (value ?? string.Empty).Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string BuildReadingViewHtml(ReadingContent content)
        {
            var title = string.IsNullOrWhiteSpace(content?.Title) ? "Reading view" : content.Title;
            var blocks = content?.Blocks ?? new List<ReadingBlock>();
            var titleBlock = blocks.FirstOrDefault(block =>
                string.Equals(block.Kind, "h1", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizeReadingText(block.Text), NormalizeReadingText(title), StringComparison.OrdinalIgnoreCase));
            var titleAttribute = titleBlock == null ? string.Empty : " data-legacy-edge-speech-index='" + titleBlock.SpeechIndex + "'";
            var html = new StringBuilder();
            html.Append("<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>")
                .Append(HtmlEncode(title))
                .Append("</title><style>*{box-sizing:border-box}body{margin:0;background:#f7f4ec;color:#282722;font-family:Georgia,'Times New Roman',serif}article{max-width:780px;margin:0 auto;padding:68px 50px 110px}h1{font:42px/1.16 'Segoe UI Light','Segoe UI',sans-serif;font-weight:300;margin:0 0 12px;color:#242424}h2{font:29px/1.3 'Segoe UI Semibold','Segoe UI',sans-serif;margin:42px 0 14px}h3{font:23px/1.35 'Segoe UI Semibold','Segoe UI',sans-serif;margin:34px 0 12px}.byline{font:14px/1.5 'Segoe UI',sans-serif;color:#6c6860;margin:0 0 38px}p,blockquote{font-size:20px;line-height:1.72;margin:0 0 24px}blockquote{margin-left:0;padding:4px 0 4px 24px;border-left:4px solid #b8afa1;color:#4c4943;font-style:italic}.list{padding-left:26px;position:relative}.list:before{content:'\\2022';position:absolute;left:6px}.caption{font:14px/1.5 'Segoe UI',sans-serif;color:#69655e;margin-top:-14px}img{display:block;max-width:100%;height:auto;margin:32px auto 24px}.legacy-edge-speaking{background:#d8eaf8;box-shadow:0 0 0 4px #d8eaf8;border-radius:1px}@media(max-width:700px){article{padding:46px 26px 80px}h1{font-size:34px}p,blockquote{font-size:18px}}</style></head><body><article><h1")
                .Append(titleAttribute).Append(">").Append(HtmlEncode(title)).Append("</h1>");

            if (!string.IsNullOrWhiteSpace(content?.Byline))
                html.Append("<div class='byline'>").Append(HtmlEncode(content.Byline)).Append("</div>");
            else
                html.Append("<div class='byline'>Reading view</div>");

            foreach (var block in blocks)
            {
                if (block == titleBlock) continue;
                if (string.Equals(block.Kind, "img", StringComparison.OrdinalIgnoreCase))
                {
                    if (Uri.TryCreate(block.Url, UriKind.Absolute, out Uri imageUri) &&
                        (imageUri.Scheme == Uri.UriSchemeHttp || imageUri.Scheme == Uri.UriSchemeHttps))
                        html.Append("<img src='").Append(HtmlEncode(imageUri.ToString())).Append("' alt='").Append(HtmlEncode(block.Alt)).Append("'>");
                    continue;
                }

                var text = NormalizeReadingText(block.Text);
                if (text.Length == 0) continue;
                var attribute = block.SpeechIndex < 0 ? string.Empty : " data-legacy-edge-speech-index='" + block.SpeechIndex + "'";
                switch ((block.Kind ?? "p").ToLowerInvariant())
                {
                    case "h1":
                    case "h2": html.Append("<h2").Append(attribute).Append(">").Append(HtmlEncode(text)).Append("</h2>"); break;
                    case "h3": html.Append("<h3").Append(attribute).Append(">").Append(HtmlEncode(text)).Append("</h3>"); break;
                    case "blockquote": html.Append("<blockquote").Append(attribute).Append(">").Append(HtmlEncode(text)).Append("</blockquote>"); break;
                    case "li": html.Append("<p class='list'").Append(attribute).Append(">").Append(HtmlEncode(text)).Append("</p>"); break;
                    case "figcaption": html.Append("<p class='caption'").Append(attribute).Append(">").Append(HtmlEncode(text)).Append("</p>"); break;
                    default: html.Append("<p").Append(attribute).Append(">").Append(HtmlEncode(text)).Append("</p>"); break;
                }
            }

            html.Append("</article></body></html>");
            return html.ToString();
        }

        private string BuildStartPageHtml(bool isPrivate)
        {
            var mode = isPrivate ? "TopSites" : BrowserDataStore.NewTabMode;
            if (mode != "TopSitesAndSuggestedContent" && mode != "TopSites" && mode != "Blank")
                mode = "TopSitesAndSuggestedContent";
            var showTopSites = mode != "Blank";
            var showSuggestedContent = !isPrivate && mode == "TopSitesAndSuggestedContent";
            var accent = isPrivate ? "#744da9" : "#0078d7";
            var candidates = new List<BrowserItem>();
            if (_favorites != null) candidates.AddRange(_favorites);
            if (!isPrivate && _history != null) candidates.AddRange(_history);
            candidates.AddRange(new[]
            {
                new BrowserItem { Title = "MSN", Url = "https://www.msn.com/", IconUrl = "https://www.msn.com/favicon.ico" },
                new BrowserItem { Title = "Bing", Url = "https://www.bing.com/", IconUrl = "https://www.bing.com/favicon.ico" },
                new BrowserItem { Title = "Wikipedia", Url = "https://en.wikipedia.org/", IconUrl = "https://en.wikipedia.org/favicon.ico" },
                new BrowserItem { Title = "Outlook", Url = "https://outlook.live.com/", IconUrl = "https://outlook.live.com/favicon.ico" },
                new BrowserItem { Title = "GitHub", Url = "https://github.com/", IconUrl = "https://github.com/favicon.ico" },
                new BrowserItem { Title = "YouTube", Url = "https://www.youtube.com/", IconUrl = "https://www.youtube.com/favicon.ico" }
            });

            var sites = candidates
                .Where(item => IsWebAddress(item.Url))
                .GroupBy(item => HostLabel(item.Url), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(8)
                .ToList();
            var colors = new[] { "#0078d7", "#107c10", "#8764b8", "#d83b01", "#008272", "#5c2d91", "#c239b3", "#486860" };
            var tiles = new StringBuilder();
            for (var index = 0; index < sites.Count; index++)
            {
                var site = sites[index];
                var label = string.IsNullOrWhiteSpace(site.Title) ? HostLabel(site.Url) : site.Title;
                if (label.Length > 28) label = label.Substring(0, 28) + "…";
                var initial = char.ToUpperInvariant(label[0]).ToString();
                var icon = Uri.TryCreate(site.IconUrl, UriKind.Absolute, out Uri iconUri) && (iconUri.Scheme == Uri.UriSchemeHttp || iconUri.Scheme == Uri.UriSchemeHttps)
                    ? "<img src='" + HtmlEncode(iconUri.ToString()) + "' alt='' onerror=\"this.style.display='none';this.nextElementSibling.style.display='flex'\"><span class='initial' style='display:none'>" + HtmlEncode(initial) + "</span>"
                    : "<span class='initial'>" + HtmlEncode(initial) + "</span>";
                tiles.Append("<a class='tile' href='").Append(HtmlEncode(site.Url)).Append("'><span class='tile-art' style='background:").Append(colors[index % colors.Length]).Append("'>").Append(icon).Append("</span><span class='tile-label'>").Append(HtmlEncode(label)).Append("</span></a>");
            }

            var suggestions = new List<BrowserItem>();
            if (_history != null) suggestions.AddRange(_history.Where(item => IsWebAddress(item.Url)).Take(8));
            suggestions.AddRange(new[]
            {
                new BrowserItem { Title = "Top stories from around the world", Url = "https://www.msn.com/en-us/news" },
                new BrowserItem { Title = "Your local weather forecast", Url = "https://www.msn.com/en-us/weather" },
                new BrowserItem { Title = "Scores, schedules, and sports news", Url = "https://www.msn.com/en-us/sports" },
                new BrowserItem { Title = "Markets and personal finance", Url = "https://www.msn.com/en-us/money" },
                new BrowserItem { Title = "Explore today's Bing images", Url = "https://www.bing.com/images" },
                new BrowserItem { Title = "Discover something new on Wikipedia", Url = "https://en.wikipedia.org/wiki/Special:Random" }
            });
            suggestions = suggestions
                .Where(item => IsWebAddress(item.Url))
                .GroupBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(8)
                .ToList();

            var storyColors = new[] { "#315f7d,#162d40", "#507d2a,#263f18", "#9b4b28,#4c2418", "#594d87,#28233f", "#1c7771,#113e3a", "#8c3d63,#472033", "#3f6f9e,#20384f", "#82672d,#433516" };
            var storyGlyphs = new[] { "&#xE8A5;", "&#xE706;", "&#xE7C1;", "&#xE8C7;", "&#xE8B7;", "&#xE774;", "&#xE81E;", "&#xE8D2;" };
            var stories = new StringBuilder();
            for (var index = 0; index < suggestions.Count; index++)
            {
                var story = suggestions[index];
                var title = string.IsNullOrWhiteSpace(story.Title) ? HostLabel(story.Url) : story.Title;
                if (title.Length > 82) title = title.Substring(0, 82) + "…";
                stories.Append("<a class='story' href='").Append(HtmlEncode(story.Url)).Append("'><span class='story-art' style='background:linear-gradient(135deg,").Append(storyColors[index % storyColors.Length]).Append(")'><span class='story-glyph'>").Append(storyGlyphs[index % storyGlyphs.Length]).Append("</span></span><span class='story-copy'><strong>").Append(HtmlEncode(title)).Append("</strong><small>").Append(HtmlEncode(HostLabel(story.Url))).Append("</small></span></a>");
            }

            var customizer = isPrivate ? string.Empty :
                "<button id='customize-button' class='customize-button' type='button' title='Customize' aria-label='Customize new tab page'>&#xE713;</button>" +
                "<div id='customizer' class='customizer' role='dialog' aria-label='Customize new tab page'><h2>Customize</h2>" +
                "<a class='choice" + (mode == "TopSitesAndSuggestedContent" ? " selected" : "") + "' href='legacyedge://newtab/TopSitesAndSuggestedContent'><span>Top sites and suggested content</span><i></i></a>" +
                "<a class='choice" + (mode == "TopSites" ? " selected" : "") + "' href='legacyedge://newtab/TopSites'><span>Top sites</span><i></i></a>" +
                "<a class='choice" + (mode == "Blank" ? " selected" : "") + "' href='legacyedge://newtab/Blank'><span>A blank page</span><i></i></a></div>";
            var privacyNote = isPrivate ? "<section class='privacy'><strong>InPrivate browsing</strong><p>Pages from this window aren't added to browsing history or session restore. This EdgeHTML recreation shares cookies and website data with normal windows, so use Clear browsing data when you need to remove site data.</p><p>Favorites and downloads are still saved, and your school, workplace, or internet service provider may still be able to see your activity.</p></section>" : string.Empty;
            var searchTemplate = EscapeJavaScript(BrowserDataStore.SearchTemplate);
            var darkAccent = isPrivate ? "#c2a5e8" : "#6cb8f1";
            var darkThemeCss = IsDarkTheme
                ? "html{background:#1f1f1f}body{background:#1f1f1f;color:#f2f2f2}.shell,.blank{background:#1f1f1f}.customize-button{color:#e6e6e6}.customize-button:hover,.customize-button:focus{background:#3a3a3a}.customizer{background:#2b2b2b;border-color:#606060;box-shadow:0 5px 18px rgba(0,0,0,.55)}.choice{color:#f1f1f1}.choice:hover{background:#3a3a3a}.choice i{border-color:#b0b0b0}.choice.selected i{border-color:" + darkAccent + "}.prompt{color:#f2f2f2}.search{background:#2b2b2b;border-color:#858585;box-shadow:0 1px 3px rgba(0,0,0,.5)}.search input{background:#2b2b2b;color:#fff}.search input::-ms-input-placeholder{color:#bcbcbc}.search input::placeholder{color:#bcbcbc}.section-title{color:#e8e8e8}.tile{background:#2b2b2b;color:#f2f2f2;border-color:#515151;box-shadow:0 1px 1px rgba(0,0,0,.35)}.tile:hover{border-color:#aaa}.tile-label{background:#2b2b2b}.privacy{background:#2b2b2b;border-top-color:" + darkAccent + ";color:#d6d6d6}.privacy strong{color:" + darkAccent + "}.feed{border-top-color:#444;background:#1b1b1b}.feed-nav{border-bottom-color:#444}.feed-nav a{color:#d0d0d0}.feed-nav a.active{color:" + darkAccent + ";border-bottom-color:" + darkAccent + "}.feed-title{color:#f2f2f2}.story{background:#2b2b2b;border-color:#515151;color:#f2f2f2}.story:hover{border-color:#aaa}.story-copy small{color:#c0c0c0}"
                : string.Empty;
            var page = new StringBuilder();
            page.Append("<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>").Append(isPrivate ? "InPrivate" : "New tab").Append("</title><style>")
                .Append("*{box-sizing:border-box}html,body{margin:0;min-height:100%;font-family:'Segoe UI',sans-serif}body{background:#f2f2f2;color:#202020}.shell{min-height:100vh;position:relative}.customize-button{position:absolute;z-index:3;right:26px;top:20px;width:36px;height:36px;border:0;background:transparent;color:#444;font:17px 'Segoe MDL2 Assets';cursor:pointer}.customize-button:hover,.customize-button:focus{background:#ddd}.customizer{display:none;position:absolute;z-index:4;right:26px;top:59px;width:300px;padding:22px 0 12px;background:#fff;border:1px solid #bbb;box-shadow:0 5px 18px rgba(0,0,0,.24)}.customizer.open{display:block}.customizer h2{font-size:20px;font-weight:400;margin:0 22px 13px}.choice{height:46px;padding:0 20px;color:#222;text-decoration:none;display:flex;align-items:center;justify-content:space-between}.choice:hover{background:#eee}.choice i{width:15px;height:15px;border:1px solid #777;border-radius:50%}.choice.selected i{border:4px solid #0078d7}.hero{padding:61px 32px 27px}.prompt{font-size:28px;font-weight:300;text-align:center;margin:0 0 18px;color:#333}.search{height:48px;max-width:680px;margin:0 auto;display:flex;background:#fff;border:1px solid #888;box-shadow:0 1px 3px rgba(0,0,0,.15)}.search input{min-width:0;flex:1;border:0;padding:0 15px;font:16px 'Segoe UI';outline:0;background:#fff;color:#222}.search button{width:50px;border:0;background:").Append(accent).Append(";color:#fff;font:18px 'Segoe MDL2 Assets';cursor:pointer}.search button:hover{filter:brightness(.92)}.top-area{max-width:1056px;margin:31px auto 0}.section-title{font-size:15px;font-weight:400;margin:0 0 10px}.sites{display:grid;grid-template-columns:repeat(8,minmax(0,1fr));gap:10px}.tile{height:101px;min-width:0;background:#fff;color:#292929;text-decoration:none;display:flex;flex-direction:column;border:1px solid #d4d4d4;box-shadow:0 1px 1px rgba(0,0,0,.08)}.tile:hover{border-color:#777}.tile-art{height:72px;display:flex;align-items:center;justify-content:center}.tile-art img,.initial{width:34px;height:34px}.tile-art img{object-fit:contain}.initial{display:flex;align-items:center;justify-content:center;color:#fff;font-size:19px}.tile-label{height:28px;padding:5px 8px;background:#fff;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:12px}.privacy{max-width:680px;margin:32px auto 0;padding:23px 26px;background:#fff;border-top:4px solid ").Append(accent).Append(";font-size:14px;line-height:1.5;color:#555}.privacy strong{font-size:21px;font-weight:400;color:").Append(accent).Append("}.privacy p{margin:10px 0 0}.feed{border-top:1px solid #ddd;background:#fff;padding:0 32px 72px}.feed-nav{max-width:1056px;height:52px;margin:0 auto;display:flex;align-items:center;border-bottom:1px solid #ddd}.feed-nav a{color:#444;text-decoration:none;font-size:13px;margin-right:27px}.feed-nav a.active{color:#0078d7;border-bottom:3px solid #0078d7;height:52px;display:flex;align-items:center}.feed-nav a.personalize{margin-left:auto;margin-right:0}.feed-title{max-width:1056px;margin:23px auto 12px;font-size:20px;font-weight:300}.stories{max-width:1056px;margin:0 auto;display:grid;grid-template-columns:repeat(4,minmax(0,1fr));grid-auto-rows:226px;gap:12px}.story{min-width:0;background:#fff;border:1px solid #d1d1d1;color:#222;text-decoration:none;display:flex;flex-direction:column;overflow:hidden}.story:hover{border-color:#777}.story-art{height:128px;display:flex;align-items:center;justify-content:center}.story-glyph{font:48px 'Segoe MDL2 Assets';color:rgba(255,255,255,.9)}.story-copy{padding:13px 15px;display:flex;min-height:0;flex:1;flex-direction:column}.story-copy strong{font-size:15px;line-height:1.28;font-weight:600;overflow:hidden}.story-copy small{margin-top:auto;color:#666;font-size:11px}.blank{min-height:100vh}@media(max-width:900px){.sites{grid-template-columns:repeat(4,minmax(0,1fr))}.stories{grid-template-columns:repeat(2,minmax(0,1fr))}}@media(max-width:560px){.hero{padding:54px 18px 25px}.prompt{font-size:24px}.top-area{margin-top:25px}.sites{grid-template-columns:repeat(2,minmax(0,1fr))}.feed{padding-left:18px;padding-right:18px}.feed-nav a{margin-right:14px}.feed-nav a:nth-of-type(4),.feed-nav a:nth-of-type(5){display:none}.stories{grid-template-columns:1fr}.customizer{right:12px;width:calc(100% - 24px)}}")
                .Append(darkThemeCss)
                .Append("</style></head><body><main class='shell'>").Append(customizer);

            if (showTopSites)
            {
                page.Append("<section class='hero'><div class='prompt'>").Append(isPrivate ? "InPrivate browsing" : "Where to next?").Append("</div><form id='search' class='search'><input id='q' autocomplete='off' spellcheck='false' aria-label='Search the web' placeholder='Search the web'><button title='Search' aria-label='Search'>&#xE721;</button></form><div class='top-area'><h1 class='section-title'>Top sites</h1><div class='sites'>").Append(tiles).Append("</div></div>").Append(privacyNote).Append("</section>");
            }
            else
            {
                page.Append("<div class='blank' aria-label='Blank new tab page'></div>");
            }

            if (showSuggestedContent)
            {
                page.Append("<section class='feed'><nav class='feed-nav'><a class='active' href='https://www.msn.com/'>My feed</a><a href='https://www.msn.com/en-us/news'>News</a><a href='https://www.msn.com/en-us/sports'>Sports</a><a href='https://www.msn.com/en-us/money'>Money</a><a href='https://www.msn.com/en-us/weather'>Weather</a><a class='personalize' href='https://www.msn.com/'>Personalize</a></nav><h2 class='feed-title'>Suggested for you</h2><div class='stories'>").Append(stories).Append("</div></section>");
            }

            page.Append("</main><script>(function(){var form=document.getElementById('search');if(form){form.onsubmit=function(e){e.preventDefault();var q=document.getElementById('q').value.trim();if(q){location.href='").Append(searchTemplate).Append("'.replace('{0}',encodeURIComponent(q));}};}var button=document.getElementById('customize-button'),panel=document.getElementById('customizer');if(button&&panel){button.onclick=function(e){e.stopPropagation();panel.className=panel.className.indexOf('open')<0?'customizer open':'customizer';};document.onclick=function(e){if(!panel.contains(e.target)&&e.target!==button)panel.className='customizer';};document.onkeydown=function(e){if(e.keyCode===27)panel.className='customizer';};}})();</script></body></html>");
            return page.ToString();
        }
    }
}
