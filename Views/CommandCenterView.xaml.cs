using System.Windows;
using System.Windows.Controls;
using FieldStation.Services;

namespace FieldStation.Views;

public partial class CommandCenterView : UserControl
{
    public CommandCenterView()
    {
        InitializeComponent();
        Loaded += (_, _) => MotionDirector.Reveal(PageRoot, 26);
    }
}
