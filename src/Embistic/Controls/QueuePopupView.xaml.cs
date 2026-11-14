using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Embistic.Models;

namespace Embistic.Controls;

public partial class QueuePopupView : UserControl
{
    private Point _dragOrigin;
    private int _dragIndex = -1;
    private Track? _dragTrack;
    private readonly string _dragFormat = "Embistic.QueueEntry." + Guid.NewGuid().ToString("N");
    public event Action? CloseRequested, ClearRequested;
    public event Action<int>? PlayRequested, RemoveRequested;
    public event Action<int, int>? MoveRequested;
    public event Action<string, bool>? CreateRequested;
    public event Action<string>? LoadRequested, DeleteRequested, UpdateRequested;
    public event Action<string, string>? RenameRequested;
    public QueuePopupView() => InitializeComponent();
    public void SetQueue(ObservableCollection<Track> queue) => QueueItems.ItemsSource = queue;
    public void UpdateSummary(string scope, Track? current, bool playing)
    {
        QueueSummary.Text = $"{QueueItems.Items.Count:N0} 首 · {scope}";
        CurrentLabel.Text = current is null ? "选择歌曲开始播放" : $"{(playing ? "正在播放" : "当前歌曲")} · {current.Title}";
    }
    public void SetPlaylists(IReadOnlyList<LocalPlaylist> playlists, string? selectedId = null)
    {
        selectedId ??= (SavedLists.SelectedItem as LocalPlaylist)?.Id;
        SavedLists.ItemsSource = playlists;
        SavedLists.SelectedValue = selectedId;
        if (SavedLists.SelectedIndex < 0 && playlists.Count > 0) SavedLists.SelectedIndex = 0;
    }
    private int IndexOfSender(object sender) => sender is DependencyObject element && Ancestor<ListBoxItem>(element) is { } row ? QueueItems.ItemContainerGenerator.IndexFromContainer(row) : -1;
    private void MoveUp_Click(object sender, RoutedEventArgs e) { var index = IndexOfSender(sender); if (index > 0) MoveRequested?.Invoke(index, index - 1); e.Handled = true; }
    private void MoveDown_Click(object sender, RoutedEventArgs e) { var index = IndexOfSender(sender); if (index >= 0 && index + 1 < QueueItems.Items.Count) MoveRequested?.Invoke(index, index + 1); e.Handled = true; }
    private void Remove_Click(object sender, RoutedEventArgs e) { var index = IndexOfSender(sender); if (index >= 0) RemoveRequested?.Invoke(index); e.Handled = true; }
    private void Queue_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Ancestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        var index = IndexOfSender(e.OriginalSource); if (index >= 0) PlayRequested?.Invoke(index);
    }
    private void Queue_KeyDown(object sender, KeyEventArgs e)
    {
        var index = QueueItems.SelectedIndex; if (index < 0) return;
        if (e.Key == Key.Delete) { RemoveRequested?.Invoke(index); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Up or Key.Down)
        {
            var target = Math.Clamp(index + (e.Key == Key.Up ? -1 : 1), 0, QueueItems.Items.Count - 1);
            if (target != index) { MoveRequested?.Invoke(index, target); QueueItems.SelectedIndex = target; }
            e.Handled = true;
        }
    }
    private void Queue_PointerDown(object sender, MouseButtonEventArgs e)
    {
        _dragIndex = -1; _dragTrack = null;
        if (Ancestor<FrameworkElement>(e.OriginalSource as DependencyObject) is not { Tag: "QueueDragHandle" }) return;
        _dragIndex = IndexOfSender(e.OriginalSource);
        if (_dragIndex >= 0) { _dragTrack = QueueItems.Items[_dragIndex] as Track; _dragOrigin = e.GetPosition(QueueItems); }
    }
    private void Queue_PointerMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragIndex < 0 || _dragTrack is null) return;
        var point = e.GetPosition(QueueItems);
        if (Math.Abs(point.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var drag = new QueueDrag(_dragIndex, _dragTrack);
        _dragIndex = -1; _dragTrack = null;
        DragDrop.DoDragDrop(QueueItems, new DataObject(_dragFormat, drag), DragDropEffects.Move);
    }
    private void Queue_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(_dragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        if (e.Effects == DragDropEffects.Move && FindVisual<ScrollViewer>(QueueItems) is { } scroll)
        {
            var y = e.GetPosition(QueueItems).Y;
            if (y < 30) scroll.LineUp(); else if (y > QueueItems.ActualHeight - 30) scroll.LineDown();
        }
        e.Handled = true;
    }
    private void Queue_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(_dragFormat) is not QueueDrag drag || drag.Index >= QueueItems.Items.Count || !ReferenceEquals(QueueItems.Items[drag.Index], drag.Track)) return;
        var target = IndexOfSender(e.OriginalSource);
        if (target < 0) target = QueueItems.Items.Count - 1;
        if (target >= 0 && target != drag.Index) { MoveRequested?.Invoke(drag.Index, target); QueueItems.SelectedIndex = target; }
        e.Handled = true;
    }
    private sealed record QueueDrag(int Index, Track Track);
    private void SavedList_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SavedLists.SelectedItem is LocalPlaylist list) { PlaylistName.Text = list.Name; SavedTracks.ItemsSource = list.Tracks; }
        else { SavedTracks.ItemsSource = null; PlaylistName.Text = ""; }
    }
    private void SaveAs_Click(object sender, RoutedEventArgs e) { Tabs.SelectedIndex = 1; PlaylistName.Text = ""; PlaylistName.Focus(); }
    private void Create_Click(object sender, RoutedEventArgs e) => CreateRequested?.Invoke(PlaylistName.Text, false);
    private void CreateFromQueue_Click(object sender, RoutedEventArgs e) => CreateRequested?.Invoke(PlaylistName.Text, true);
    private void Load_Click(object sender, RoutedEventArgs e) { if (SavedLists.SelectedItem is LocalPlaylist list) { LoadRequested?.Invoke(list.Id); Tabs.SelectedIndex = 0; } }
    private void Rename_Click(object sender, RoutedEventArgs e) { if (SavedLists.SelectedItem is LocalPlaylist list) RenameRequested?.Invoke(list.Id, PlaylistName.Text); }
    private void Update_Click(object sender, RoutedEventArgs e) { if (SavedLists.SelectedItem is LocalPlaylist list) UpdateRequested?.Invoke(list.Id); }
    private void Delete_Click(object sender, RoutedEventArgs e) { if (SavedLists.SelectedItem is LocalPlaylist list) DeleteRequested?.Invoke(list.Id); }
    private void Clear_Click(object sender, RoutedEventArgs e) => ClearRequested?.Invoke();
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    private static T? Ancestor<T>(DependencyObject? value) where T : DependencyObject
    {
        while (value is not null) { if (value is T match) return match; value = value is Visual ? VisualTreeHelper.GetParent(value) : LogicalTreeHelper.GetParent(value); } return null;
    }
    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); if (child is T match) return match; if (FindVisual<T>(child) is { } found) return found; } return null;
    }
}
