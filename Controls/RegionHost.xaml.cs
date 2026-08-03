using System.Windows;
using System.Windows.Controls;
using FieldStation.Extensibility;

namespace FieldStation.Controls;

/// <summary>Resolves a named RegionRegistry factory or displays an explicit extension placeholder.</summary>
public partial class RegionHost : UserControl
{
    public static readonly DependencyProperty RegionKeyProperty = DependencyProperty.Register(
        nameof(RegionKey), typeof(string), typeof(RegionHost), new PropertyMetadata(string.Empty, OnRegionChanged));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(RegionHost), new PropertyMetadata("自定义区域"));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(RegionHost), new PropertyMetadata("注册控件后将替换此占位内容。"));

    public RegionHost()
    {
        InitializeComponent();
        Loaded += (_, _) => Resolve();
    }

    public string RegionKey { get => (string)GetValue(RegionKeyProperty); set => SetValue(RegionKeyProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    private static void OnRegionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is RegionHost host && host.IsLoaded) host.Resolve();
    }

    private void Resolve()
    {
        if (!string.IsNullOrWhiteSpace(RegionKey) && RegionRegistry.Default.TryCreate(RegionKey, out var element))
        {
            ResolvedContent.Content = element;
            ResolvedContent.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;
            EmptyFrame.Visibility = Visibility.Collapsed;
        }
        else
        {
            ResolvedContent.Content = null;
            ResolvedContent.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            EmptyFrame.Visibility = Visibility.Visible;
        }
    }
}
