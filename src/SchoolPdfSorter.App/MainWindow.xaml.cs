using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SchoolPdfSorter.Engine;
using SchoolPdfSorter.ViewModels;

namespace SchoolPdfSorter;

public partial class MainWindow : Window
{
    private const double BottomTolerance = 60;

    private readonly ChatViewModel _vm;
    private readonly ChatSession _session;
    private ScrollViewer? _scroller;
    private bool _pinnedToBottom = true;

    public MainWindow()
    {
        InitializeComponent();

        // WPF sizes are device-independent pixels while SystemParameters.WorkArea
        // is in physical pixels. On a 768p laptop at 125% scaling the default
        // 760-DIP window is taller than the work area, which pushes the input bar
        // off-screen. Keep the whole window visible.
        var dpi = VisualTreeHelper.GetDpi(this);
        var workArea = SystemParameters.WorkArea;
        var workWidth = workArea.Width / Math.Max(dpi.DpiScaleX, 0.1);
        var workHeight = workArea.Height / Math.Max(dpi.DpiScaleY, 0.1);
        Width = Math.Min(Width, Math.Max(MinWidth, workWidth - 32));
        Height = Math.Min(Height, Math.Max(MinHeight, workHeight - 32));

        _vm = new ChatViewModel();
        _session = new ChatSession(_vm);
        _vm.Session = _session;
        DataContext = _vm;

        _vm.Messages.CollectionChanged += OnMessagesChanged;
        ChatList.SizeChanged += OnTranscriptSizeChanged;
        Loaded += OnWindowLoaded;

        InputBox.PreviewKeyDown += OnInputPreviewKeyDown;
    }

    // ------------------------------------------------------------------ flow

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnWindowLoaded;
        ApplyDarkTitleBar();
        ScrollToBottom();
        InputBox.Focus();

        // A broken build can be started with --rollback to put the saved previous one back.
        if (UpdateFeed.WantsRollback(Environment.GetCommandLineArgs()) &&
            await _session.TryRollbackAsync())
            return;

        _session.StartUpdateCheck();
        _session.StartUsagePing();
        _session.StartFlow();
    }

    /// <summary>Match the OS title bar to the app's dark palette.</summary>
    private void ApplyDarkTitleBar()
    {
        try
        {
            var handle = new WindowInteropHelper(this).EnsureHandle();
            var dark = 1;

            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE on Windows 11, 19 on Windows 10 1809+.
            if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Older Windows: keep the stock title bar.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // -------------------------------------------------------------- scrolling

    private ScrollViewer? GetScroller()
    {
        if (_scroller is null)
        {
            // FindName("PART_ScrollViewer") does not resolve against the theme
            // namescope, so walk the visual tree instead.
            var scroller = FindScroller(ChatList);

            if (scroller is not null)
            {
                _scroller = scroller;
                scroller.ScrollChanged += OnScrollChanged;
            }
        }

        return _scroller;
    }

    private static ScrollViewer? FindScroller(DependencyObject node)
    {
        if (node is ScrollViewer scroller)
            return scroller;

        var count = VisualTreeHelper.GetChildrenCount(node);

        for (var i = 0; i < count; i++)
        {
            var found = FindScroller(VisualTreeHelper.GetChild(node, i));
            if (found is not null)
                return found;
        }

        return null;
    }

    private void ScrollToBottom()
    {
        if (GetScroller() is not { } scroller)
            return;

        ChatList.UpdateLayout();
        scroller.ScrollToEnd();
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Only user-initiated scrolling (or our own ScrollToEnd) changes the offset;
        // content growing underneath an idle viewport must not un-pin the view.
        if (e.VerticalChange == 0 || GetScroller() is not { } scroller)
            return;

        var distance = scroller.ExtentHeight - scroller.VerticalOffset - scroller.ViewportHeight;
        _pinnedToBottom = distance <= BottomTolerance;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset))
            return;

        if (_pinnedToBottom)
            Dispatcher.InvokeAsync(ScrollToBottom);
    }

    private void OnTranscriptSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_pinnedToBottom)
            ScrollToBottom();
    }

    // ------------------------------------------------------------------ input

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        // Shift+Enter inserts a line break; the parser accepts either shape.
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            return;

        e.Handled = true;
        _vm.SendCommand.Execute(null);
    }

    /// <summary>
    /// Ctrl+V with a file copied in Explorer: the clipboard holds a file list, which a TextBox
    /// silently ignores. Treat it as drag-and-drop, the folder at step 1/3, the course-structure
    /// file at step 2.
    /// </summary>
    private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        try
        {
            if (!Clipboard.ContainsFileDropList())
                return;

            var paths = Clipboard.GetFileDropList();

            if (paths.Count == 0 || paths[0] is not { Length: > 0 } path)
                return;

            e.Handled = true;
            _vm.SubmitPath(path);
        }
        catch (Exception)
        {
            // The clipboard can be locked by another app for a moment; Ctrl+V still works
            // for text on the next try.
        }
    }

    // --------------------------------------------------------------- support

    /// <summary>Copy button inside the support section (copies the bank number).</summary>
    private void OnCopyAccountNumber(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string number || number.Length == 0)
            return;

        try
        {
            Clipboard.SetText(number);
        }
        catch
        {
            // Clipboard can be busy for a moment; nothing useful to tell the user.
            return;
        }

        // Brief "Copied" confirmation on the button itself.
        button.Content = "Copied";
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        timer.Tick += (_, _) =>
        {
            button.Content = "Copy number";
            timer.Stop();
        };
        timer.Start();
    }

    /// <summary>Open button inside the support section (opens the sealed source link).</summary>
    private void OnOpenRepository(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string url || url.Length == 0)
            return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // No browser registered, or the machine blocks it: nothing useful to tell the user.
            return;
        }

        // Brief "Opened" confirmation on the button itself.
        button.Content = "Opened";
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        timer.Tick += (_, _) =>
        {
            button.Content = "Open";
            timer.Stop();
        };
        timer.Start();
    }

    // ------------------------------------------------------------- drag & drop

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
            return;

        _vm.SubmitPath(paths[0]);
        InputBox.Focus();
    }
}
