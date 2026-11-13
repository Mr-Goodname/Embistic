using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace Embistic.Services;

/// <summary>A native notification icon with WPF transport controls.</summary>
public sealed class TrayService : IDisposable
{
    private const int Callback = 0x8000 + 42;
    private static readonly uint RestoreMessage = RegisterWindowMessage("Embistic.RestoreMainWindow.v1");
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly Window _window;
    private readonly HwndSource _source;
    private readonly IntPtr _icon;
    private readonly bool _ownsIcon;
    private readonly ContextMenu _menu = new();
    private readonly MenuItem _play;
    private NotifyIconData _data;
    private bool _disposed;
    public bool Available { get; private set; }
    public event Action? RestoreRequested;
    public event Action? TogglePlayRequested;
    public event Action? PreviousRequested;
    public event Action? NextRequested;
    public event Action? ExitRequested;
    public TrayService(Window window)
    {
        _window = window;
        var hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("窗口尚未初始化。");
        _icon = ExtractIcon(IntPtr.Zero, Environment.ProcessPath!, 0);
        _ownsIcon = _icon != IntPtr.Zero && _icon != new IntPtr(1);
        if (!_ownsIcon) _icon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
        _data = new NotifyIconData { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = hwnd, Id = 1, Flags = 1 | 2 | 4,
            CallbackMessage = Callback, Icon = _icon, Tip = "Embistic · 音乐播放", Info = "", InfoTitle = "" };
        _source.AddHook(Hook);
        Add("打开 Embistic", () => RestoreRequested?.Invoke());
        _menu.Items.Add(new Separator());
        _play = Add("播放", () => TogglePlayRequested?.Invoke());
        Add("上一首", () => PreviousRequested?.Invoke());
        Add("下一首", () => NextRequested?.Invoke());
        _menu.Items.Add(new Separator());
        Add("退出 Embistic", () => ExitRequested?.Invoke());
        Available = ShellNotifyIcon(0, ref _data);
    }
    private MenuItem Add(string text, Action action)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => action();
        _menu.Items.Add(item);
        return item;
    }
    public static void RequestExistingWindow() => RequestExistingWindow(new IntPtr(0xffff));
    public static void RequestExistingWindow(IntPtr targetWindow) => PostMessage(targetWindow, RestoreMessage, IntPtr.Zero, IntPtr.Zero);
    public void Update(string title, bool playing)
    {
        if (_disposed) return;
        _play.Header = playing ? "暂停" : "播放";
        var text = "Embistic · " + (playing ? "正在播放 · " : "已暂停 · ") + title;
        _data.Tip = text[..Math.Min(text.Length, 127)];
        if (Available) ShellNotifyIcon(1, ref _data);
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == RestoreMessage) { RestoreRequested?.Invoke(); handled = true; }
        else if ((uint)message == _taskbarCreated) Available = ShellNotifyIcon(0, ref _data);
        else if (message == Callback)
        {
            var mouseMessage = unchecked((int)lParam.ToInt64()) & 0xffff;
            if (mouseMessage is 0x202 or 0x203) RestoreRequested?.Invoke();
            else if (mouseMessage == 0x205)
            {
                SetForegroundWindow(hwnd);
                _menu.Placement = PlacementMode.MousePoint;
                _menu.IsOpen = true;
            }
            handled = true;
        }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _menu.IsOpen = false;
        ShellNotifyIcon(2, ref _data);
        _source.RemoveHook(Hook);
        if (_ownsIcon) DestroyIcon(_icon);
        Available = false;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint command, ref NotifyIconData data);
    [DllImport("shell32.dll", EntryPoint = "ExtractIconW", CharSet = CharSet.Unicode)] private static extern IntPtr ExtractIcon(IntPtr instance, string file, uint index);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr icon);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
