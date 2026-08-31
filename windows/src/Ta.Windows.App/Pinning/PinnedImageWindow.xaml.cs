using System.Drawing;
using System.Windows.Input;
using Ta.Windows.App.Imaging;
using Ta.Windows.Platform.Clipboard;

namespace Ta.Windows.App.Pinning;

public partial class PinnedImageWindow : System.Windows.Window
{
    private readonly Bitmap image;
    private readonly IWindowsClipboardService clipboardService;

    public PinnedImageWindow(Bitmap image, IWindowsClipboardService clipboardService)
    {
        InitializeComponent();
        this.image = image;
        this.clipboardService = clipboardService;
        PinnedImage.Source = BitmapSourceConverter.Create(image);
    }

    public long PixelCount => checked((long)image.Width * image.Height);

    protected override void OnClosed(EventArgs e)
    {
        image.Dispose();
        base.OnClosed(e);
    }

    private void Window_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Button ||
            e.OriginalSource is System.Windows.Controls.Primitives.Thumb)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            Close();
            return;
        }

        DragMove();
    }

    private void Window_OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var factor = e.Delta > 0 ? 1.08 : 0.92;
        Width = Math.Clamp(Width * factor, MinWidth, 1800);
        Height = Math.Clamp(Height * factor, MinHeight, 1200);
        e.Handled = true;
    }

    private void CopyButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            clipboardService.SetImageExplicit(image);
            PinStatusText.Visibility = System.Windows.Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            PinStatusText.Text = $"复制没有完成，贴图仍然保留。下一步：{exception.Message}";
            PinStatusText.Visibility = System.Windows.Visibility.Visible;
        }
    }

    private void OpacitySlider_OnValueChanged(
        object sender,
        System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsLoaded)
        {
            Opacity = e.NewValue;
        }
    }

    private void CloseButton_OnClick(object sender, System.Windows.RoutedEventArgs e) => Close();
}
