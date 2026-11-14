using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Data;
using System.Windows.Threading;
using Embistic.Models;
using Embistic.Services;
using Microsoft.Win32;

namespace Embistic.Controls;

/// <summary>Inline source configuration; valid edits are persisted automatically by the owner.</summary>
public sealed class LyricsSourcesEditor : UserControl
{
    private readonly ObservableCollection<NetworkLyricsSource> _sources;
    private readonly ComboBox _list = new() { DisplayMemberPath = "Name", Margin = new Thickness(0, 8, 0, 8) };
    private readonly StackPanel _fields = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
    private readonly DispatcherTimer _changes = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly Func<Track?> _currentTrack;
    private bool _building, _refreshingNames;
    private string _lastPublished;
    public bool IsInputValid { get; private set; } = true;
    public event Action<List<NetworkLyricsSource>>? SourcesChanged;
    public List<NetworkLyricsSource> Result => _sources.Select(s => s.Clone()).ToList();

    public LyricsSourcesEditor(IEnumerable<NetworkLyricsSource> sources, Func<Track?> currentTrack)
    {
        _currentTrack = currentTrack;
        _sources = new(sources.Select(s => s.Clone())); _list.ItemsSource = _sources;
        _lastPublished = JsonSerializer.Serialize(Result);
        AutomationProperties.SetAutomationId(_list, "LyricsSourceSelection");
        var root = new DockPanel();
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top);
        header.Children.Add(new TextBlock { Text = "按顺序匹配；Emby 与本地歌词优先，未匹配时使用内置来源。", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        header.Children.Add(_list);
        var actions = new WrapPanel();
        void Add(string label, Action action) { var button = new Button { Content = label, Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(0, 0, 5, 5) }; button.Click += (_, _) => action(); actions.Children.Add(button); }
        Add("添加", () =>
        {
            if (_sources.Count >= 16) { _status.Text = "最多 16 个来源"; return; }
            var source = new NetworkLyricsSource { Name = "新来源", UrlTemplate = "https://lrclib.net/api/get?track_name={title}&artist_name={artist}&duration={duration}", LyricsPath = "syncedLyrics" };
            _sources.Add(source); _list.SelectedItem = source;
        });
        Add("删除", () => { if (_list.SelectedItem is NetworkLyricsSource s) { var index = _list.SelectedIndex; _sources.Remove(s); _list.SelectedIndex = Math.Min(index, _sources.Count - 1); } });
        Add("上移", () => Move(-1)); Add("下移", () => Move(1));
        Add("导入", Import); Add("导出", Export);
        header.Children.Add(actions); root.Children.Add(header);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom);
        var test = new Button { Content = "测试当前歌曲", Margin = new Thickness(0, 8, 0, 0) };
        test.Click += async (_, _) =>
        {
            Flush();
            if (_currentTrack() is not { } track) { _status.Text = "先播放一首歌曲，再测试来源。"; return; }
            if (_list.SelectedItem is not NetworkLyricsSource source) return;
            test.IsEnabled = false; _status.Text = "正在测试…";
            try { Validate(source); var result = await NetworkLyricsProvider.FetchAsync(source.Clone(), track); _status.Text = string.IsNullOrWhiteSpace(result.PlainText) ? "未找到匹配的歌词，请检查接口和字段。" : result.Source + " · " + (result.IsSynced ? "同步歌词" : "文本歌词") + "\n" + string.Join("\n", result.PlainText.Split('\n').Take(2)); }
            catch (Exception ex) when (ex is ArgumentException or HttpRequestException or TaskCanceledException) { _status.Text = "测试失败，请检查接口地址和网络。"; }
            finally { test.IsEnabled = true; }
        };
        footer.Children.Add(test); footer.Children.Add(_status); root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = _fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        _list.SelectionChanged += (_, _) => { if (!_refreshingNames) BuildFields(); };
        _sources.CollectionChanged += (_, _) => QueueChanges();
        _changes.Tick += (_, _) => Flush();
        Unloaded += (_, _) => Flush();
        Content = root;
        _list.SelectedIndex = _sources.Count > 0 ? 0 : -1; BuildFields();
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
    }
    public void Flush()
    {
        if (!_changes.IsEnabled) return;
        _changes.Stop();
        try
        {
            var result = ValidatedResult();
            IsInputValid = true;
            _status.Text = "修改即时生效并自动保存";
            var snapshot = JsonSerializer.Serialize(result);
            if (snapshot == _lastPublished) return;
            _lastPublished = snapshot;
            SourcesChanged?.Invoke(result);
            // Refresh the selector label without replacing the field controls or
            // their focus/caret while an interface is being typed.
            _refreshingNames = true;
            try
            {
                var selected = _list.SelectedItem;
                _list.Items.Refresh();
                if (selected is not null) _list.SelectedItem = selected;
            }
            finally { _refreshingNames = false; }
        }
        catch (ArgumentException ex) { IsInputValid = false; _status.Text = ex.Message; }
    }
    private void QueueChanges() { if (_building) return; _changes.Stop(); _changes.Start(); }
    private void Move(int delta) { var index = _list.SelectedIndex; if (index < 0 || index + delta < 0 || index + delta >= _sources.Count) return; _sources.Move(index, index + delta); _list.SelectedIndex = index + delta; }
    private void BuildFields()
    {
        _building = true;
        try
        {
            _fields.Children.Clear();
            if (_list.SelectedItem is not NetworkLyricsSource source) { _fields.Children.Add(new TextBlock { Text = "点击“添加”配置网络歌词接口。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) }); return; }
            _fields.DataContext = source;
            void Field(string label, string property)
            {
                _fields.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 6) });
                var box = new TextBox { MaxLength = 4096 };
                AutomationProperties.SetAutomationId(box, "LyricsSource" + property); AutomationProperties.SetName(box, label);
                box.SetBinding(TextBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
                box.TextChanged += (_, _) => QueueChanges(); _fields.Children.Add(box);
            }
            void Check(string label, string property)
            {
                var box = new CheckBox { Content = label, Margin = new Thickness(0, 8, 0, 0) };
                AutomationProperties.SetAutomationId(box, "LyricsSource" + property); AutomationProperties.SetName(box, label);
                box.SetBinding(CheckBox.IsCheckedProperty, new Binding(property) { Mode = BindingMode.TwoWay });
                box.Checked += (_, _) => QueueChanges(); box.Unchecked += (_, _) => QueueChanges(); _fields.Children.Add(box);
            }
            Check("启用此来源", nameof(source.Enabled)); Field("来源名称", nameof(source.Name)); Field("歌词 GET 接口模板", nameof(source.UrlTemplate));
            _fields.Children.Add(new TextBlock { Text = "占位符：{title} {artist} {album} {duration}（秒）{durationMs} {id}", TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });
            Field("歌词字段（纯文本留空；JSON 如 data.lyric）", nameof(source.LyricsPath));
            Field("搜索接口（可选）", nameof(source.SearchUrlTemplate)); Field("搜索结果数组字段（根数组留空）", nameof(source.SearchResultsPath));
            Field("歌曲 ID 字段", nameof(source.IdPath)); Field("歌名字段", nameof(source.TitlePath)); Field("歌手字段", nameof(source.ArtistPath)); Field("时长字段（可选）", nameof(source.DurationPath));
            Check("搜索结果时长使用毫秒", nameof(source.DurationIsMilliseconds));
        }
        finally { _building = false; }
    }
    private List<NetworkLyricsSource> ValidatedResult()
    {
        var result = Result;
        foreach (var source in result) { source.Normalize(); if (source.Enabled) Validate(source); }
        return result;
    }

    public void ImportConfiguration(string json)
    {
        var imported = JsonSerializer.Deserialize<List<NetworkLyricsSource>>(json) ?? throw new JsonException();
        if (imported.Count > 16 || imported.Any(s => s is null)) throw new JsonException();
        foreach (var source in imported) { source.Normalize(); if (source.Enabled) Validate(source); }
        _sources.Clear(); foreach (var source in imported) _sources.Add(source);
        _list.SelectedIndex = _sources.Count > 0 ? 0 : -1;
        Flush();
    }

    public string ExportConfiguration()
    {
        Flush();
        return JsonSerializer.Serialize(ValidatedResult(), new JsonSerializerOptions { WriteIndented = true });
    }
    private void Validate(NetworkLyricsSource source)
    {
        var track = _currentTrack() ?? new Track { Id = "1", Title = "示例歌曲", Artist = "示例歌手", DurationMs = 180000 };
        if (string.IsNullOrWhiteSpace(source.UrlTemplate)) throw new ArgumentException("请填写歌词接口地址，填写完成后自动保存。");
        NetworkLyricsProvider.BuildUrl(source.UrlTemplate, track, "1");
        if (source.SearchUrlTemplate.Length > 0)
        {
            NetworkLyricsProvider.BuildUrl(source.SearchUrlTemplate, track);
            if (!source.UrlTemplate.Contains("{id}") || source.TitlePath.Length == 0 || source.ArtistPath.Length == 0 || source.IdPath.Length == 0)
                throw new ArgumentException("搜索需填写歌名、歌手及 ID 字段，歌词接口使用 {id}。");
        }
    }
    private void Import()
    {
        var picker = new OpenFileDialog { Filter = "歌词来源配置|*.json" }; if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            ImportConfiguration(File.ReadAllText(picker.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { _status.Text = "无法导入，请检查配置文件内容。"; }
    }
    private void Export()
    {
        string json;
        try { json = ExportConfiguration(); }
        catch (ArgumentException ex) { _status.Text = ex.Message; return; }
        var picker = new SaveFileDialog { Filter = "歌词来源配置|*.json", FileName = "Embistic-lyrics-sources.json" }; if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        try { File.WriteAllText(picker.FileName, json); _status.Text = "配置已导出。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text = "无法写入，请选择其他位置。"; }
    }
}
