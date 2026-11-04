using System.Windows;

namespace Luma.Island;

public partial class MainWindow
{
    private void OnCompactExpand(object sender, RoutedEventArgs e)
    {
        if (_dragged) { _dragged = false; return; }
        ToggleExpanded();
    }
}
