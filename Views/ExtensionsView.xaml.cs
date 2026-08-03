using System.Windows;
using System.Windows.Controls;
using FieldStation.Services;

namespace FieldStation.Views;

public partial class ExtensionsView : UserControl
{
    public ExtensionsView()
    {
        InitializeComponent();
        Loaded += (_, _) => MotionDirector.Reveal(PageRoot, 24);
    }
}
