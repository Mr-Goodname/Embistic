using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Embistic.Controls;

internal static class Program
{
    private static int _checks;
    private static Window? _host;
    private static int _moves, _buttons, _keys;
    private static Point _lastPointer;
    private static MouseButtonState _lastButton;
    private static Key _lastKey;
    private static bool _stressInput;

    [STAThread]
    private static int Main(string[] args)
    {
        _stressInput = args.Contains("--stress-input");
        if (_stressInput) Console.WriteLine("Fixture stress: add 60 ms of dispatcher work while the mouse button is held.");
        GetCursorPos(out var originalCursor); var foreground = GetForegroundWindow();
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            LoadProductionResources(app);
            foreach (var scale in new[] { 1.0, 1.5, 2.0 }) TestHorizontal(scale);
            TestVertical(false); TestVertical(true);
            Console.WriteLine($"PASS: {_checks} production slider template and native mouse input checks.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            SendMouse(0x0004, waitForDelivery: false); Mouse.Capture(null); _host?.Close(); app.Shutdown();
            SetCursorPos(originalCursor.X, originalCursor.Y);
            if (foreground != IntPtr.Zero) SetForegroundWindow(foreground);
        }
    }

    private static void LoadProductionResources(Application app)
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Embistic", "App.xaml"))) directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Embistic source root not found.");
        var source = XElement.Load(Path.Combine(directory.FullName, "src", "Embistic", "App.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = new XElement(presentation + "ResourceDictionary",
            source.Attributes().Where(attribute => attribute.IsNamespaceDeclaration).Select(attribute =>
                new XAttribute(attribute.Name, attribute.Value.StartsWith("clr-namespace:", StringComparison.Ordinal)
                    ? attribute.Value + ";assembly=SliderChecks" : attribute.Value)),
            source.Element(presentation + "Application.Resources")!.Elements().Where(element =>
                element.Name.LocalName == "SolidColorBrush" || element.Name.LocalName == "Style" && (string?)element.Attribute("TargetType") == "Slider"));
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _checks++; Console.WriteLine("PASS: " + name);
    }

    private static void Show(Slider slider, double scale = 1)
    {
        _host?.Close();
        var content = new Border
        {
            Child = slider, Padding = new Thickness(25), Background = Brushes.Black,
            LayoutTransform = new ScaleTransform(scale, scale)
        };
        _host = new Window
        {
            Content = content, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Left = SystemParameters.WorkArea.Left + 30, Top = SystemParameters.WorkArea.Top + 30,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            Opacity = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase) ? 1 : .04
        };
        _moves = _buttons = _keys = 0;
        _host.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler((_, e) =>
        {
            _moves++; _lastPointer = _host.PointToScreen(e.GetPosition(_host));
        }), true);
        _host.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, _) =>
        { _buttons++; _lastButton = MouseButtonState.Pressed; }), true);
        _host.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) =>
        { _buttons++; _lastButton = MouseButtonState.Released; }), true);
        _host.AddHandler(UIElement.PreviewKeyUpEvent, new KeyEventHandler((_, e) => { _keys++; _lastKey = e.Key; }), true);
        _host.Show();
        slider.ApplyTemplate();
        _host.UpdateLayout(); slider.UpdateLayout();
        var hwnd = new WindowInteropHelper(_host).Handle;
        SetForegroundWindow(hwnd);
        WaitFor(() => GetForegroundWindow() == hwnd, "Fixture window did not become foreground");
    }

    private static void TestHorizontal(double scale)
    {
        var source = new ValueSource { Value = 10 };
        var slider = new Slider { Width = 300, Height = 44, Minimum = 0, Maximum = 100, SmallChange = 1 };
        // Attach release listeners before the implicit style enables the rail behavior,
        // matching the ordering of handlers in a XAML-created SeekBar.
        var releases = 0; var releaseValue = -1.0;
        slider.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(async (_, _) =>
        {
            releases++;
            await slider.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            releaseValue = slider.Value;
        }), true);
        slider.SetBinding(Slider.ValueProperty, new Binding(nameof(ValueSource.Value)) { Source = source, Mode = BindingMode.TwoWay });
        Show(slider, scale);
        var track = TrackOf(slider);
        Check(SliderDragBehavior.GetEnabled(slider) && track.Thumb.ActualWidth == 24,
            $"Production slider template and drag behavior load at {scale:P0} logical scaling");
        MoveTo(slider, .2); SendMouse(0x0002); Pump(25);
        Check(slider.IsMouseCaptured && Math.Abs(slider.Value - 20) < 1.5,
            $"Rail click captures and jumps to the pointer at {scale:P0}");
        MoveTo(slider, .75);
        Check(Math.Abs(slider.Value - 75) < 1.5, $"Rail click continues as a drag at {scale:P0}");
        SendMouse(0x0004); Pump(35);
        Check(!slider.IsMouseCaptured && releases == 1 && Math.Abs(releaseValue - 75) < 1.5,
            $"Seek-style release sees the final rail value exactly once at {scale:P0}");
        Check(BindingOperations.IsDataBound(slider, Slider.ValueProperty) && Math.Abs(source.Value - 75) < 1.5,
            $"Rail dragging preserves and updates the two-way value binding at {scale:P0}");

        slider.Value = 30; slider.UpdateLayout();
        MovePointer(track.Thumb.PointToScreen(new Point(track.Thumb.ActualWidth / 2, track.Thumb.ActualHeight / 2)));
        SendMouse(0x0002); Pump(20);
        Check(track.Thumb.IsDragging && !slider.IsMouseCaptured, $"Native Thumb dragging remains intact at {scale:P0}");
        MoveTo(slider, .7); SendMouse(0x0004); Pump(25);
        Console.WriteLine($"THUMB: scale={scale:P0}, expected=70, actual={slider.Value:0.000}, dragging={track.Thumb.IsDragging}, captured={Mouse.Captured?.GetType().Name ?? "none"}, sliderCapture={slider.IsMouseCaptured}, pointer={Mouse.GetPosition(track)}");
        Check(!track.Thumb.IsDragging && Math.Abs(slider.Value - 70) < 2,
            $"Native Thumb movement updates value and releases capture at {scale:P0} (value={slider.Value:0.000}, dragging={track.Thumb.IsDragging}, captured={Mouse.Captured?.GetType().Name ?? "none"})");

        MoveTo(slider, .15); SendMouse(0x0002); Pump(20);
        var outside = slider.PointToScreen(new Point(slider.ActualWidth + 100, slider.ActualHeight / 2));
        MovePointer(outside); SendMouse(0x0004); Pump(25);
        Check(slider.Value == 100 && !slider.IsMouseCaptured,
            $"A drag released outside the slider clamps and ends reliably at {scale:P0}");

        slider.IsSnapToTickEnabled = true; slider.TickFrequency = 10;
        MoveTo(slider, .34); SendMouse(0x0002); Pump(15); SendMouse(0x0004); Pump(20);
        Check(slider.Value == 30, $"Rail dragging honors tick-frequency snapping at {scale:P0}");
        slider.Ticks = new DoubleCollection { 0, 20, 55, 100 };
        MoveTo(slider, .4); SendMouse(0x0002); Pump(15); SendMouse(0x0004); Pump(20);
        Check(slider.Value == 55, $"Rail dragging honors explicit tick positions at {scale:P0}");
        slider.IsSnapToTickEnabled = false;

        MoveTo(slider, .1); SendMouse(0x0002); Pump(15);
        SliderDragBehavior.SetEnabled(slider, false);
        Check(!slider.IsMouseCaptured, $"Disabling the behavior releases an in-progress rail capture at {scale:P0}");
        SendMouse(0x0004); Pump(15); SliderDragBehavior.SetEnabled(slider, true);
        slider.IsDirectionReversed = true; Pump(15);
        MoveTo(slider, .3); SendMouse(0x0002); Pump(15); MoveTo(slider, .85); SendMouse(0x0004); Pump(20);
        Check(Math.Abs(slider.Value - 85) < 1.5, $"Reversed horizontal direction remains correct at {scale:P0}");
        slider.IsDirectionReversed = false;
        MoveTo(slider, .15); SendMouse(0x0002); Pump(15);
        var beforeLostCapture = slider.Value;
        Mouse.Capture(null); Pump(15); MoveTo(slider, .9);
        Check(!slider.IsMouseCaptured && Math.Abs(slider.Value - beforeLostCapture) < .001,
            $"External capture loss cancels the rail drag without later phantom movement at {scale:P0}");
        SendMouse(0x0004); Pump(15);
        slider.IsEnabled = false;
        var disabledValue = slider.Value;
        MoveTo(slider, .6); SendMouse(0x0002); Pump(15); SendMouse(0x0004); Pump(15);
        Check(slider.Value == disabledValue && !slider.IsMouseCaptured, $"Disabled sliders ignore rail clicks at {scale:P0}");
        slider.IsEnabled = true; slider.Value = 20; slider.Focus(); Pump(15);
        SendKey(0x27); Pump(20);
        Check(slider.Value == 21, $"Arrow-key adjustment remains native at {scale:P0}");
        SendKey(0x24); Pump(20);
        Check(slider.Value == 0, $"Home-key adjustment remains native at {scale:P0}");
    }

    private static void TestVertical(bool reversed)
    {
        var slider = new Slider
        {
            Width = 44, Height = 260, Orientation = Orientation.Vertical,
            Minimum = 0, Maximum = 100, Value = 10, IsDirectionReversed = reversed
        };
        Show(slider); var track = TrackOf(slider);
        Check(track.ActualHeight > 240 && track.ActualWidth == 24,
            $"Vertical template stretches its rail along the slider (reversed={reversed})");
        var rail = Find<Border>(track.DecreaseRepeatButton, value => value.Name == "Rail");
        Check(rail.ActualWidth == 3 && rail.ActualHeight > 0,
            $"Vertical rail paints as a thin vertical line (reversed={reversed})");
        MoveTo(slider, .25); SendMouse(0x0002); Pump(20); MoveTo(slider, .8); SendMouse(0x0004); Pump(25);
        Check(Math.Abs(slider.Value - 80) < 1.5 && !slider.IsMouseCaptured,
            $"Vertical rail click continues dragging and releases correctly (reversed={reversed})");
        slider.Value = 35; slider.UpdateLayout();
        MovePointer(track.Thumb.PointToScreen(new Point(12, 12))); SendMouse(0x0002); Pump(15);
        Check(track.Thumb.IsDragging, $"Vertical Thumb retains native drag behavior (reversed={reversed})");
        MoveTo(slider, .65); SendMouse(0x0004); Pump(20);
        Console.WriteLine($"THUMB: vertical reversed={reversed}, expected=65, actual={slider.Value:0.000}, dragging={track.Thumb.IsDragging}, captured={Mouse.Captured?.GetType().Name ?? "none"}");
        Check(Math.Abs(slider.Value - 65) < 2 && !track.Thumb.IsDragging,
            $"Vertical Thumb follows the pointer in the selected direction (reversed={reversed})");
    }

    private static Track TrackOf(Slider slider) => (Track)slider.Template.FindName("PART_Track", slider);
    private static void MoveTo(Slider slider, double fraction)
    {
        slider.UpdateLayout();
        var track = TrackOf(slider);
        Point local;
        if (slider.Orientation == Orientation.Horizontal)
        {
            var position = slider.IsDirectionReversed ? 1 - fraction : fraction;
            local = new Point(track.Thumb.ActualWidth / 2 + (track.ActualWidth - track.Thumb.ActualWidth) * position, track.ActualHeight / 2);
        }
        else
        {
            var position = slider.IsDirectionReversed ? fraction : 1 - fraction;
            local = new Point(track.ActualWidth / 2, track.Thumb.ActualHeight / 2 + (track.ActualHeight - track.Thumb.ActualHeight) * position);
        }
        MovePointer(track.PointToScreen(local));
    }

    private static T Find<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        if (root is T value && predicate(value)) return value;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            try { return Find(VisualTreeHelper.GetChild(root, i), predicate); } catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException(typeof(T).Name + " not found.");
    }

    private static void MovePointer(Point point)
    {
        var rounded = new Point(Math.Round(point.X), Math.Round(point.Y));
        GetCursorPos(out var before); var serial = _moves;
        if (_stressInput && Mouse.LeftButton == MouseButtonState.Pressed)
            _host!.Dispatcher.BeginInvoke(DispatcherPriority.Send, () => Thread.Sleep(60));
        if (!SetCursorPos((int)rounded.X, (int)rounded.Y)) throw new InvalidOperationException("Native cursor movement was rejected.");
        // An unchanged cursor position need not generate a WM_MOUSEMOVE. Otherwise
        // wait for the actual routed move before releasing the button: a timer alone
        // can expire while input remains queued on a busy hosted runner.
        if (before.X != rounded.X || before.Y != rounded.Y)
            WaitFor(() => _moves > serial && (_lastPointer - rounded).Length <= 1,
                $"Native cursor move was not delivered (target={rounded}, delivered={_lastPointer}, moves={_moves}, captured={Mouse.Captured?.GetType().Name ?? "none"})");
        _host!.UpdateLayout();
    }
    private static void SendMouse(uint flags, bool waitForDelivery = true)
    {
        var serial = _buttons;
        var input = new Input { Type = 0, Data = new InputData { Mouse = new MouseInput { Flags = flags } } };
        if (SendInput(1, new[] { input }, Marshal.SizeOf<Input>()) != 1) throw new InvalidOperationException("Native mouse input was rejected.");
        if (waitForDelivery && _host?.IsLoaded == true)
        {
            var state = flags == 0x0002 ? MouseButtonState.Pressed : MouseButtonState.Released;
            WaitFor(() => _buttons > serial && _lastButton == state, $"Native mouse {state} event was not delivered");
            _host.UpdateLayout();
        }
    }
    private static void SendKey(ushort key)
    {
        var serial = _keys;
        var inputs = new[]
        {
            new Input { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { Key = key } } },
            new Input { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { Key = key, Flags = 2 } } }
        };
        if (SendInput(2, inputs, Marshal.SizeOf<Input>()) != 2) throw new InvalidOperationException("Native keyboard input was rejected.");
        WaitFor(() => _keys > serial && _lastKey == KeyInterop.KeyFromVirtualKey(key), "Native keyboard release event was not delivered");
    }
    private static void WaitFor(Func<bool> completed, string description)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!completed() && deadline.ElapsedMilliseconds < 2000) Pump(5);
        if (!completed()) throw new InvalidOperationException($"{description}; deliveredPointer={_lastPointer}, moves={_moves}, buttons={_buttons}, captured={Mouse.Captured?.GetType().Name ?? "none"}");
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }

    private sealed class ValueSource : INotifyPropertyChanged
    {
        private double _value;
        public double Value { get => _value; set { _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr ExtraInfo; }
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
