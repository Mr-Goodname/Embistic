using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Embistic.Models;
using Embistic.Services;
using Microsoft.Win32;

namespace Embistic.Views;

public sealed class LyricsSourcesWindow : Window
{
    private readonly ObservableCollection<NetworkLyricsSource> _sources;
    private readonly ListBox _list;
    private readonly StackPanel _fields = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,14,0,0) };
    private readonly Track? _track;
    public List<NetworkLyricsSource> Result => _sources.Select(source => source.Clone()).ToList();
    public LyricsSourcesWindow(IEnumerable<NetworkLyricsSource> sources, Track? track)
    {
        Title = "网络歌词来源"; Width = 940; Height = 780; MinWidth = 760; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _track = track;
        _sources = new(sources.Select(source => source.Clone()));
        var root = new DockPanel { Margin = new Thickness(26) };
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top);
        header.Children.Add(new TextBlock { Text = "自定义网络歌词来源", FontSize = 23, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "按列表顺序匹配，失败后自动使用 LRCLIB / 网易云。手动导入与 Emby 同步歌词优先。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,10,0,14) });
        root.Children.Add(header);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); footer.Children.Add(_status);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,18,0,0) };
        Button Action(string label, Action action) { var button = new Button { Content = label, Margin = new Thickness(8,0,0,0) }; button.Click += (_, _) => action(); actions.Children.Add(button); return button; }
        Action("导入配置", Import); Action("导出配置", Export);
        var test = Action("测试当前歌曲", () => { }); test.Click += async (_, _) =>
        {
            if (_track is null) { _status.Text = "先播放一首歌曲，再测试歌词来源。"; return; }
            if (_list?.SelectedItem is not NetworkLyricsSource source) return;
            test.IsEnabled = false; _status.Text = "正在测试…";
            try
            {
                Validate(source);
                var result = await NetworkLyricsProvider.FetchAsync(source.Clone(), _track);
                _status.Text = string.IsNullOrWhiteSpace(result.PlainText) ? "未获得匹配的歌词，请检查接口模板和响应字段。" : $"{result.Source} · {(result.IsSynced ? "同步歌词" : "文本歌词")}\n" + string.Join("\n", result.PlainText.Split('\n').Take(3));
            }
            catch (ArgumentException ex) { _status.Text = ex.Message; }
            finally { test.IsEnabled = true; }
        };
        var cancel = Action("取消", () => { DialogResult = false; }); cancel.IsCancel = true;
        var save = Action("保存", () =>
        {
            try { foreach (var source in _sources) { source.Normalize(); if (source.Enabled) Validate(source); } DialogResult = true; }
            catch (ArgumentException ex) { _status.Text = ex.Message; }
        }); save.IsDefault = true;
        footer.Children.Add(actions); root.Children.Add(footer);
        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) }); body.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new DockPanel { Margin = new Thickness(0,0,20,0) };
        var moves = new WrapPanel { Margin = new Thickness(0,12,0,0) }; DockPanel.SetDock(moves, Dock.Bottom);
        void SidebarButton(string label, Action action) { var button = new Button { Content = label, Padding = new Thickness(12,8,12,8), Margin = new Thickness(0,0,5,5) }; button.Click += (_, _) => action(); moves.Children.Add(button); }
        _list = new ListBox { ItemsSource = _sources, DisplayMemberPath = "Name" };
        SidebarButton("添加", () => { if (_sources.Count >= 16) { _status.Text = "最多可配置 16 个来源。"; return; } var source = new NetworkLyricsSource { Name = "新来源", UrlTemplate = "https://lrclib.net/api/get?track_name={title}&artist_name={artist}&duration={duration}", LyricsPath = "syncedLyrics" }; _sources.Add(source); _list.SelectedItem = source; });
        SidebarButton("删除", () => { if (_list.SelectedItem is NetworkLyricsSource source) _sources.Remove(source); });
        SidebarButton("↑", () => Move(-1)); SidebarButton("↓", () => Move(1));
        sidebar.Children.Add(moves); sidebar.Children.Add(_list); body.Children.Add(sidebar);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _fields }; Grid.SetColumn(scroll, 1); body.Children.Add(scroll);
        _list.SelectionChanged += (_, _) => BuildFields();
        root.Children.Add(body); Content = root; _list.SelectedIndex = _sources.Count > 0 ? 0 : -1; BuildFields();
    }
    private void Move(int delta) { var index = _list.SelectedIndex; if (index < 0 || index + delta < 0 || index + delta >= _sources.Count) return; _sources.Move(index, index + delta); _list.SelectedIndex = index + delta; }
    private void BuildFields()
    {
        _fields.Children.Clear();
        if (_list.SelectedItem is not NetworkLyricsSource source) { _fields.Children.Add(new TextBlock { Text = "点击“添加”配置歌词接口。没有自定义来源时继续使用内置来源。", TextWrapping = TextWrapping.Wrap }); return; }
        _fields.DataContext = source;
        void Check(string label, string property) { var box = new CheckBox { Content = label }; box.SetBinding(CheckBox.IsCheckedProperty, new Binding(property) { Mode = BindingMode.TwoWay }); _fields.Children.Add(box); }
        void Field(string label, string property) { _fields.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0,11,0,6) }); var box = new TextBox(); box.SetBinding(TextBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }); _fields.Children.Add(box); }
        Check("启用此来源", nameof(source.Enabled));
        Field("来源名称", nameof(source.Name)); Field("歌词 GET 接口地址模板", nameof(source.UrlTemplate));
        _fields.Children.Add(new TextBlock { Text = "占位符：{title} {artist} {album} {duration}（秒）{durationMs} {id}。所有值会自动 URL 编码。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0,8,0,0) });
        Field("歌词响应字段（文本 LRC / YRC / TTML 留空；JSON 如 data.lyric）", nameof(source.LyricsPath));
        _fields.Children.Add(new TextBlock { Text = "搜索后获取歌词（可选）", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,24,0,0) });
        Field("搜索 GET 地址模板（无需搜索时留空）", nameof(source.SearchUrlTemplate));
        Field("搜索结果数组字段（数组在根节点时留空）", nameof(source.SearchResultsPath));
        Field("歌曲 ID 字段", nameof(source.IdPath)); Field("歌名字段", nameof(source.TitlePath)); Field("歌手字段（如 artists[0].name）", nameof(source.ArtistPath)); Field("时长字段（可选）", nameof(source.DurationPath)); Check("搜索结果的时长单位为毫秒", nameof(source.DurationIsMilliseconds));
        _fields.Children.Add(new TextBlock { Text = "搜索结果需匹配歌名和歌手；填写时长字段后还会校验版本长度。接口返回的真实逐字时间会保留。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0,12,0,12) });
    }
    private void Validate(NetworkLyricsSource source)
    {
        var track = _track ?? new Track { Id = "1", Title = "示例歌曲", Artist = "示例歌手", DurationMs = 180000 };
        if (source.UrlTemplate.Length == 0) throw new ArgumentException(source.Name + "：请填写歌词接口。");
        NetworkLyricsProvider.BuildUrl(source.UrlTemplate, track, "1");
        if (source.SearchUrlTemplate.Length > 0)
        {
            NetworkLyricsProvider.BuildUrl(source.SearchUrlTemplate, track);
            if (!source.UrlTemplate.Contains("{id}") || source.TitlePath.Length == 0 || source.ArtistPath.Length == 0 || source.IdPath.Length == 0)
                throw new ArgumentException(source.Name + "：搜索模式需填写歌名、歌手、ID 字段，并在歌词接口使用 {id}。");
        }
    }
    private void Import()
    {
        var picker = new OpenFileDialog { Filter = "歌词来源配置|*.json" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var sources = JsonSerializer.Deserialize<List<NetworkLyricsSource>>(File.ReadAllText(picker.FileName)) ?? throw new JsonException();
            if (sources.Count > 16 || sources.Any(source => source is null)) throw new JsonException();
            foreach (var source in sources) source.Normalize();
            _sources.Clear(); foreach (var source in sources) _sources.Add(source); _list.SelectedIndex = 0; _status.Text = "配置已导入，保存后生效。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _status.Text = "无法读取歌词来源配置。"; }
    }
    private void Export()
    {
        var picker = new SaveFileDialog { Filter = "歌词来源配置|*.json", FileName = "Embistic-lyrics-sources.json" };
        if (picker.ShowDialog(this) != true) return;
        try { File.WriteAllText(picker.FileName, JsonSerializer.Serialize(Result, new JsonSerializerOptions { WriteIndented = true })); _status.Text = "配置已导出。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text = "配置无法写入，请更换保存位置。"; }
    }
}
