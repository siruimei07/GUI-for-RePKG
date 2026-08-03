using System.Windows;
using System.Windows.Controls;
using FieldStation.Services;

namespace FieldStation.Views;

public partial class TopologyView : UserControl
{
    public TopologyView()
    {
        InitializeComponent();
        Loaded += (_, _) => MotionDirector.Reveal(PageRoot, -28);
    }
}
