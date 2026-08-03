using System.Windows;
using System.Windows.Controls;
using FieldStation.Services;

namespace FieldStation.Views;

public partial class ReportsView : UserControl
{
    public ReportsView()
    {
        InitializeComponent();
        Loaded += (_, _) => MotionDirector.Reveal(PageRoot, -24);
    }
}
