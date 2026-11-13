using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Embistic.Models;

namespace Embistic.Views;

public sealed class DisplaySettingsWindow : Window
{
    private readonly DisplaySettings _editable;
    private readonly StackPanel _home = new(), _library = new();
    private readonly CheckBox _compact, _sidebar, _start, _disc, _volume;
    private readonly Slider _startup;
    public DisplaySettings Result { get; private set; }
    public DisplaySettingsWindow(DisplaySettings settings)
    {
        Title = "Embistic · 界面与播放"; Width = 800; Height = 730; MinWidth = 700; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bg"); SetResourceReference(ForegroundProperty, "Text");
        Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(TabTheme));
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 14;
        _editable = settings.Clone(); _editable.Normalize(); Result = _editable;
        var root = new DockPanel { Margin = new Thickness(24) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,18,0,0) };
        DockPanel.SetDock(actions, Dock.Bottom);
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0,0,10,0) }; cancel.Click += (_,_) => DialogResult = false;
        var save = new Button { Content = "保存", IsDefault = true, Style = (Style)Application.Current.FindResource("Primary") }; save.Click += Save_Click;
        actions.Children.Add(cancel); actions.Children.Add(save); root.Children.Add(actions);
        var tabs = new TabControl { Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0) };
        var homeScroll = new ScrollViewer { Content = _home, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var libScroll = new ScrollViewer { Content = _library, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        tabs.Items.Add(new TabItem { Header = "首页内容", Content = homeScroll }); tabs.Items.Add(new TabItem { Header = "媒体库入口", Content = libScroll });
        var playback = new StackPanel { Margin = new Thickness(12) };
        _compact = Check("紧凑歌曲列表", _editable.CompactTrackRows); _sidebar = Check("显示右侧正在播放面板", _editable.ShowSidebarPlayer); _start = Check("打开程序时进入沉浸歌词", _editable.StartInLyrics); _disc = Check("播放时旋转唱片", _editable.DiscAnimation); _volume = Check("记住上次音量", _editable.RememberVolume);
        foreach (var box in new[] { _compact, _sidebar, _start, _disc, _volume }) playback.Children.Add(box);
        playback.Children.Add(new TextBlock { Text = "固定启动音量（关闭记住音量后使用）", Margin = new Thickness(0,18,0,10) });
        _startup = new Slider { Minimum = 0, Maximum = 100, Value = _editable.StartupVolume, TickFrequency = 1, IsSnapToTickEnabled = true, IsEnabled = !_editable.RememberVolume }; playback.Children.Add(_startup);
        _volume.Checked += (_, _) => _startup.IsEnabled = false;
        _volume.Unchecked += (_, _) => _startup.IsEnabled = true;
        tabs.Items.Add(new TabItem { Header = "界面与播放", Content = playback });
        Populate(_home, new Dictionary<string,string> { ["favorites"]="喜爱专辑", ["recentAlbums"]="最近添加专辑", ["recentTracks"]="最近播放歌曲", ["frequentTracks"]="最常播放歌曲", ["playedAlbums"]="最近播放专辑", ["frequentAlbums"]="最常播放专辑" }, _editable.HomeSections);
        Populate(_library, new Dictionary<string,string> { ["albums"]="专辑",["albumArtists"]="专辑艺术家",["genres"]="流派",["tracks"]="曲目",["playlists"]="播放列表",["folders"]="文件夹",["radio"]="网络电台",["composers"]="作曲家",["artists"]="所有艺术家",["compilations"]="合辑",["years"]="年份",["labels"]="音乐厂牌" }, _editable.LibraryCategories);
        root.Children.Add(tabs); Content = root;
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _editable.HomeSections = CheckedIds(_home);
        _editable.LibraryCategories = CheckedIds(_library);
        _editable.CompactTrackRows = _compact.IsChecked == true;
        _editable.ShowSidebarPlayer = _sidebar.IsChecked == true;
        _editable.StartInLyrics = _start.IsChecked == true;
        _editable.DiscAnimation = _disc.IsChecked == true;
        _editable.RememberVolume = _volume.IsChecked == true;
        _editable.StartupVolume = (int)_startup.Value;
        _editable.Normalize();
        Result = _editable;
        DialogResult = true;
    }
    private static CheckBox Check(string text, bool value) => new() { Content = text, IsChecked = value, Margin = new Thickness(0,12,0,12) };
    private const string TabTheme = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Style TargetType="TabItem">
                <Setter Property="Foreground" Value="{DynamicResource Muted}"/>
                <Setter Property="Cursor" Value="Hand"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabItem">
                    <Border x:Name="TabSurface" Background="Transparent" CornerRadius="9" Padding="16,11" Margin="0,0,7,0">
                        <ContentPresenter ContentSource="Header"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsSelected" Value="True"><Setter TargetName="TabSurface" Property="Background" Value="{DynamicResource AccentSurface}"/><Setter Property="Foreground" Value="{DynamicResource AccentInk}"/></Trigger>
                        <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="TabSurface" Property="Opacity" Value=".8"/></Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
            </Style>
            <Style TargetType="TabControl">
                <Setter Property="Background" Value="Transparent"/>
                <Setter Property="BorderThickness" Value="0"/>
                <Setter Property="Foreground" Value="{DynamicResource Text}"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabControl">
                    <Grid><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
                        <TabPanel IsItemsHost="True" Margin="0,0,0,16"/>
                        <ContentPresenter Grid.Row="1" ContentSource="SelectedContent"/>
                    </Grid>
                </ControlTemplate></Setter.Value></Setter>
            </Style>
        </ResourceDictionary>
        """;
    private static List<string> CheckedIds(StackPanel panel) => panel.Children.OfType<DockPanel>().Select(p=>p.Children.OfType<CheckBox>().First()).Where(c=>c.IsChecked==true).Select(c=>(string)c.Tag).ToList();
    private static void Populate(StackPanel panel, Dictionary<string,string> names, List<string> chosen)
    {
        panel.Margin = new Thickness(12);
        panel.Children.Add(new TextBlock { Text = "勾选要显示的内容；用上下按钮调整顺序。", Margin = new Thickness(0,10,0,14), TextWrapping = TextWrapping.Wrap });
        var order = chosen.Concat(names.Keys.Except(chosen)).ToList();
        foreach (var id in order)
        {
            var row = new DockPanel { Margin = new Thickness(0,4,0,4) };
            var moves = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(moves, Dock.Right);
            var up = new Button { Content = "↑", Padding = new Thickness(12,7,12,7), Margin = new Thickness(4,0,0,0) };
            var down = new Button { Content = "↓", Padding = new Thickness(12,7,12,7), Margin = new Thickness(4,0,0,0) };
            up.Click += (_,_)=>{ var index=panel.Children.IndexOf(row); if(index>1){panel.Children.Remove(row);panel.Children.Insert(index-1,row);} };
            down.Click += (_,_)=>{ var index=panel.Children.IndexOf(row); if(index<panel.Children.Count-1){panel.Children.Remove(row);panel.Children.Insert(index+1,row);} };
            moves.Children.Add(up); moves.Children.Add(down); row.Children.Add(moves);
            row.Children.Add(new CheckBox { Content = names[id], Tag = id, IsChecked = chosen.Contains(id), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(row);
        }
    }
}
