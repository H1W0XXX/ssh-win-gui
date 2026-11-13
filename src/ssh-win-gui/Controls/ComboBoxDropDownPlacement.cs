using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace RsyncShell.App.Controls;

/// <summary>
/// Keeps a ComboBox drop-down from opening on top of its own ComboBox.
/// </summary>
/// <remarks>
/// The WPF-UI drop-down reserves a transparent margin below the list for its shadow. WPF decides
/// whether the list fits below the ComboBox without that margin, but then keeps the whole popup
/// window (margin included) on screen by pushing it up. When the list fits and the margin does
/// not, the list ends up covering the ComboBox, so the mouse-up of the click that opened it
/// lands on an item: the selection changes and the list closes immediately. Placing the popup
/// ourselves lets the margin take part in the "does it fit below" decision.
/// </remarks>
internal static class ComboBoxDropDownPlacement
{
    /// <remarks>
    /// SizeChanged is used instead of Loaded because Loaded is not raised for a ComboBox that
    /// starts out collapsed, while every ComboBox gets a size (and by then its template) before
    /// the user can open it.
    /// </remarks>
    public static void RegisterForAllComboBoxes() =>
        EventManager.RegisterClassHandler(
            typeof(ComboBox),
            FrameworkElement.SizeChangedEvent,
            new SizeChangedEventHandler((sender, _) => Attach((ComboBox)sender)));

    public static void Attach(ComboBox comboBox)
    {
        var popup = (comboBox.Template?.FindName("Popup", comboBox) ??
                     comboBox.Template?.FindName("PART_Popup", comboBox)) as Popup;
        if (popup is null || popup.Placement == PlacementMode.Custom)
        {
            return;
        }

        popup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
            Place(comboBox, popup, popupSize, targetSize, offset);
        popup.Placement = PlacementMode.Custom;
    }

    /// <summary>
    /// Orders the "below" and "above" positions, in device pixels relative to the top-left of the
    /// placement target. WPF takes the first one that is completely on screen and then moves it
    /// by the popup's own offset (<paramref name="gap"/>).
    /// </summary>
    internal static CustomPopupPlacement[] Order(
        double popupHeight,
        double targetHeight,
        double gap,
        double leftInset,
        double bottomInset,
        double targetTop,
        double screenTop,
        double screenBottom)
    {
        var below = new CustomPopupPlacement(
            new Point(-leftInset, targetHeight),
            PopupPrimaryAxis.Horizontal);
        // Twice the gap: once to cancel the offset WPF adds, once to leave the same gap above.
        var above = new CustomPopupPlacement(
            new Point(-leftInset, -popupHeight - (2 * gap)),
            PopupPrimaryAxis.Horizontal);

        var fitsBelow = targetTop + targetHeight + gap + popupHeight + bottomInset <= screenBottom;
        var fitsAbove = targetTop - gap - popupHeight >= screenTop;
        return fitsBelow || !fitsAbove ? [below, above] : [above, below];
    }

    private static CustomPopupPlacement[] Place(
        ComboBox comboBox,
        Popup popup,
        Size popupSize,
        Size targetSize,
        Point offset)
    {
        var target = popup.PlacementTarget as FrameworkElement ?? comboBox;
        var scale = VisualTreeHelper.GetDpi(target).DpiScaleY;
        var inset = (popup.Child as FrameworkElement)?.Margin ?? default;
        var gap = offset.Y * scale;
        var leftInset = inset.Left * scale;
        var bottomInset = inset.Bottom * scale;

        if (PresentationSource.FromVisual(target) is null)
        {
            return Order(popupSize.Height, targetSize.Height, gap, leftInset, bottomInset, 0, 0, double.MaxValue);
        }

        var topLeft = target.PointToScreen(new Point(0, 0));
        var screenTop = double.MinValue;
        var screenBottom = double.MaxValue;
        var monitor = MonitorFromPoint(
            new NativePoint
            {
                X = (int)(topLeft.X + targetSize.Width / 2),
                Y = (int)(topLeft.Y + targetSize.Height / 2),
            },
            MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            // WPF keeps ComboBox popups inside the whole monitor, not just its work area.
            screenTop = info.Monitor.Top;
            screenBottom = info.Monitor.Bottom;
        }

        return Order(
            popupSize.Height,
            targetSize.Height,
            gap,
            leftInset,
            bottomInset,
            topLeft.Y,
            screenTop,
            screenBottom);
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }
}
