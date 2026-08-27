using System.Net.Http;
using Ta.Windows.Core.Capture;
using Ta.Windows.Platform.Capture;
using Ta.Windows.Platform.HotKeys;
using Ta.Windows.Platform.Vision;
using Ta.Windows.Core.Settings;

namespace Ta.Windows.Platform.Tests;

public sealed class VirtualDesktopServiceTests
{
    [Fact]
    public void ReturnsPhysicalVirtualDesktopIncludingNegativeCoordinates()
    {
        var values = new Dictionary<VirtualDesktopMetric, int>
        {
            [VirtualDesktopMetric.Left] = -1920,
            [VirtualDesktopMetric.Top] = -100,
            [VirtualDesktopMetric.Width] = 4480,
            [VirtualDesktopMetric.Height] = 1540,
        };
        var service = new VirtualDesktopService(metric => values[metric]);

        var bounds = service.GetBounds();

        Assert.Equal(new CaptureArea(-1920, -100, 4480, 1540), bounds);
    }

    [Fact]
    public void RejectsInvalidSystemMetrics()
    {
        var service = new VirtualDesktopService(_ => 0);

        Assert.Throws<InvalidOperationException>(() => service.GetBounds());
    }

    [Fact]
    public void CaptureRejectsAreaOutsideVirtualDesktopBeforeAllocatingBitmap()
    {
        var desktop = new FakeVirtualDesktopService(new CaptureArea(0, 0, 1920, 1080));
        var service = new GdiScreenCaptureService(desktop);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => service.Capture(new CaptureArea(-1, 0, 100, 100)));
    }

    [Fact]
    public void CaptureRejectsImagesLargerThanSafetyBudget()
    {
        var desktop = new FakeVirtualDesktopService(new CaptureArea(0, 0, 30000, 30000));
        var service = new GdiScreenCaptureService(desktop, maximumPixelCount: 200_000_000);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => service.Capture(new CaptureArea(0, 0, 20000, 20000)));
    }

    private sealed class FakeVirtualDesktopService(CaptureArea bounds) : IVirtualDesktopService
    {
        public CaptureArea GetBounds() => bounds;
    }
}

public sealed class HotKeyGestureParserTests
{
    [Fact]
    public void ParsesRequestedShiftARegionShortcut()
    {
        var parsed = HotKeyGestureParser.TryParse("Shift+A", out var gesture, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal(HotKeyModifiers.Shift, gesture.Modifiers);
        Assert.Equal(System.Windows.Input.Key.A, gesture.Key);
        Assert.Equal("Shift+A", gesture.ToString());
    }

    [Fact]
    public void RejectsBareKeyThatWouldInterceptNormalTyping()
    {
        var parsed = HotKeyGestureParser.TryParse("A", out _, out var error);

        Assert.False(parsed);
        Assert.Contains("修饰键", error);
    }
}

public sealed class LiveWindowsCaptureTests
{
    [Fact]
    [Trait("Category", "Live")]
    public void CapturesRealVirtualDesktopPixelsWhenExplicitlyEnabled()
    {
        var desktopService = new VirtualDesktopService();
        var bounds = desktopService.GetBounds();
        var width = Math.Min(96, bounds.Width);
        var height = Math.Min(96, bounds.Height);
        var area = new CaptureArea(bounds.X, bounds.Y, width, height);
        var service = new GdiScreenCaptureService(desktopService);

        using var bitmap = service.Capture(area);

        Assert.Equal(width, bitmap.Width);
        Assert.Equal(height, bitmap.Height);
        Assert.Equal(System.Drawing.Imaging.PixelFormat.Format32bppPArgb, bitmap.PixelFormat);
    }
}

public sealed class VisionApiClientTests
{
    [Fact]
    public async Task SendsConfiguredBaseUrlModelPromptAndBearerKey()
    {
        var handler = new RecordingHandler(
            "{\"choices\":[{\"message\":{\"content\":\"HELLO 2026\"}}]}");
        var client = new VisionApiClient(new HttpClient(handler));
        var settings = CreateSettings("https://vision.example.test/v1");
        using var bitmap = new System.Drawing.Bitmap(20, 20);

        var result = await client.AnalyzeAsync(bitmap, settings, "test-secret", CancellationToken.None);

        Assert.Equal("https://vision.example.test/v1/chat/completions", handler.RequestUri);
        Assert.Equal("Bearer test-secret", handler.Authorization);
        Assert.Contains("glm-4v-flash", handler.RequestBody);
        Assert.Contains("configured prompt", handler.RequestBody);
        Assert.Equal("HELLO 2026", result.Text);
        Assert.True(result.CloudUploaded);
    }

    [Fact]
    public async Task RejectsPlainHttpForRemoteHosts()
    {
        var client = new VisionApiClient(new HttpClient(new RecordingHandler("{}")));
        using var bitmap = new System.Drawing.Bitmap(20, 20);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => client.AnalyzeAsync(
                bitmap,
                CreateSettings("http://vision.example.test/v1"),
                "test-secret",
                CancellationToken.None));

        Assert.Contains("HTTPS", exception.Message);
    }

    private static VisionApiSettings CreateSettings(string baseUrl) =>
        new(baseUrl, "glm-4v-flash", "configured prompt", 30, 2400, 90);

    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }

        public string? Authorization { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson),
            };
        }
    }
}

public sealed class HotKeyOwnershipLiveTests
{
    [Fact]
    [Trait("Category", "HotKeyProbe")]
    public void RunningApplicationOwnsConfiguredCtrlZ()
    {
        bool? available = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var service = new GlobalHotKeyService();
                available = service.TryRegister(
                    990,
                    new HotKeyGesture(HotKeyModifiers.Control, System.Windows.Input.Key.Z),
                    () => { },
                    out _);
                if (available == true)
                {
                    service.Unregister(990);
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.False(available, "Ctrl+Z 仍可被另一个进程注册，说明运行中的拓没有持有该快捷键。");
    }

    [Theory]
    [Trait("Category", "HotKeyAvailability")]
    [InlineData("Ctrl+Z")]
    [InlineData("Ctrl+Alt+Shift+1")]
    [InlineData("Shift+A")]
    [InlineData("Shift+W")]
    [InlineData("Ctrl+Alt+Shift+3")]
    [InlineData("Ctrl+Alt+Shift+4")]
    public void ConfiguredShortcutCanBeRegisteredWhenApplicationIsStopped(string value)
    {
        Assert.True(HotKeyGestureParser.TryParse(value, out var gesture, out var parseError), parseError);
        bool? registered = null;
        string? registrationError = null;
        var thread = new Thread(() =>
        {
            using var service = new GlobalHotKeyService();
            registered = service.TryRegister(991, gesture, () => { }, out registrationError);
            if (registered == true)
            {
                service.Unregister(991);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.True(registered, $"{value} 注册失败：{registrationError}");
    }
}
