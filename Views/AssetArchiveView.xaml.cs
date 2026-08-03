using System.Windows;
using System.Windows.Controls;
using FieldStation.Services;

namespace FieldStation.Views;

public partial class AssetArchiveView : UserControl
{
    public AssetArchiveView()
    {
        InitializeComponent();
        Loaded += (_, _) => MotionDirector.Reveal(PageRoot, 30);
    }
}
