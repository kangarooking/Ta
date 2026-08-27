using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Ta.Windows.Core.Capture;
using Ta.Windows.Platform.Display;
using Ta.Windows.Platform.Interop;

namespace Ta.Windows.Platform.Capture;

public enum WindowTargetMode
{
    Window,
    Object,
}

public sealed record WindowTarget(
    nint WindowHandle,
    CaptureArea Area,
    string Label,
    WindowTargetMode Mode);

public interface IWindowTargetResolver
{
    WindowTarget? Resolve(
        PhysicalScreenPoint point,
        nint excludedWindowHandle,
        WindowTargetMode mode);
}

public sealed class WindowTargetResolver(IVirtualDesktopService virtualDesktopService)
    : IWindowTargetResolver
{
    private const int ExtendedFrameBoundsAttribute = 9;
    private const int MaximumAutomationElements = 1500;

    public WindowTarget? Resolve(
        PhysicalScreenPoint point,
        nint excludedWindowHandle,
        WindowTargetMode mode)
    {
        var topWindow = FindTopLevelWindow(point, excludedWindowHandle);
        if (topWindow == nint.Zero || !TryGetWindowArea(topWindow, out var windowArea))
        {
            return null;
        }

        var desktopArea = virtualDesktopService.GetBounds();
        var clippedWindowArea = windowArea.Intersect(desktopArea);
        if (clippedWindowArea is null)
        {
            return null;
        }

        var label = ReadWindowTitle(topWindow);
        if (mode == WindowTargetMode.Object &&
            TryResolveAutomationObject(topWindow, point, desktopArea, out var objectArea, out var objectLabel))
        {
            return new WindowTarget(
                topWindow,
                objectArea,
                string.IsNullOrWhiteSpace(objectLabel) ? label : objectLabel,
                WindowTargetMode.Object);
        }

        return new WindowTarget(
            topWindow,
            clippedWindowArea.Value,
            label,
            WindowTargetMode.Window);
    }

    private static nint FindTopLevelWindow(PhysicalScreenPoint point, nint excludedWindowHandle)
    {
        var result = nint.Zero;
        NativeMethods.EnumWindows((windowHandle, _) =>
        {
            if (windowHandle == excludedWindowHandle ||
                !NativeMethods.IsWindowVisible(windowHandle) ||
                NativeMethods.IsIconic(windowHandle) ||
                !TryGetWindowArea(windowHandle, out var area) ||
                point.X < area.X || point.X >= area.Right ||
                point.Y < area.Y || point.Y >= area.Bottom)
            {
                return true;
            }

            result = windowHandle;
            return false;
        }, nint.Zero);
        return result;
    }

    private static bool TryGetWindowArea(nint windowHandle, out CaptureArea area)
    {
        NativeMethods.NativeRect rectangle;
        var dwmResult = NativeMethods.DwmGetWindowAttribute(
            windowHandle,
            ExtendedFrameBoundsAttribute,
            out rectangle,
            Marshal.SizeOf<NativeMethods.NativeRect>());
        if (dwmResult < 0 && !NativeMethods.GetWindowRect(windowHandle, out rectangle))
        {
            area = default;
            return false;
        }

        var width = rectangle.Right - rectangle.Left;
        var height = rectangle.Bottom - rectangle.Top;
        if (width < 4 || height < 4)
        {
            area = default;
            return false;
        }

        area = new CaptureArea(rectangle.Left, rectangle.Top, width, height);
        return true;
    }

    private static bool TryResolveAutomationObject(
        nint topWindow,
        PhysicalScreenPoint point,
        CaptureArea desktopArea,
        out CaptureArea area,
        out string label)
    {
        area = default;
        label = string.Empty;
        try
        {
            var root = AutomationElement.FromHandle(topWindow);
            var screenPoint = new System.Windows.Point(point.X, point.Y);
            var best = FindDeepestContainingElement(root, screenPoint, out _);
            if (best is null)
            {
                return false;
            }

            var bounds = best.Current.BoundingRectangle;
            if (bounds.IsEmpty || bounds.Width < 4 || bounds.Height < 4)
            {
                return false;
            }

            var objectArea = new CaptureArea(
                checked((int)Math.Floor(bounds.Left)),
                checked((int)Math.Floor(bounds.Top)),
                checked((int)Math.Ceiling(bounds.Width)),
                checked((int)Math.Ceiling(bounds.Height)));
            var clipped = objectArea.Intersect(desktopArea);
            if (clipped is null)
            {
                return false;
            }

            area = clipped.Value;
            label = best.Current.Name;
            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static AutomationElement? FindDeepestContainingElement(
        AutomationElement root,
        System.Windows.Point point,
        out int visited)
    {
        visited = 0;
        AutomationElement? best = null;
        var bestArea = double.MaxValue;
        var stack = new Stack<AutomationElement>();
        stack.Push(root);
        while (stack.Count > 0 && visited < MaximumAutomationElements)
        {
            var current = stack.Pop();
            visited++;
            System.Windows.Rect bounds;
            try
            {
                bounds = current.Current.BoundingRectangle;
            }
            catch (ElementNotAvailableException)
            {
                continue;
            }

            if (!bounds.IsEmpty && bounds.Contains(point))
            {
                var candidateArea = bounds.Width * bounds.Height;
                if (candidateArea >= 16 && candidateArea < bestArea)
                {
                    best = current;
                    bestArea = candidateArea;
                }

                for (var child = TreeWalker.RawViewWalker.GetFirstChild(current);
                     child is not null;
                     child = TreeWalker.RawViewWalker.GetNextSibling(child))
                {
                    stack.Push(child);
                }
            }
        }

        return best;
    }

    private static string ReadWindowTitle(nint windowHandle)
    {
        var length = NativeMethods.GetWindowTextLength(windowHandle);
        if (length <= 0)
        {
            return "软件窗口";
        }

        var value = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(windowHandle, value, value.Capacity);
        return value.ToString();
    }
}
