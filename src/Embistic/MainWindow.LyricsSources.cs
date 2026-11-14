using System.Windows;
using Embistic.Views;

namespace Embistic;

public partial class MainWindow
{
    private void LyricsSources_Click(object sender, RoutedEventArgs e)
    {
        OpenLyricsSettings(sources: true);
    }
}
