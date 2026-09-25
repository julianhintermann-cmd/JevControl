using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kairo.Core.Abstractions;
using Kairo.Core.Models;
using Kairo.Core.Telemetry;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;
using static Kairo.Windows.Interop.NativeMethods;

namespace Kairo.Windows.Capture;

/// <summary>Raw BGRA32 image in physical screen pixels.</summary>
internal sealed record RawImage(byte[] Pixels, int Width, int Height, ScreenRect ScreenRegion);

/// <summary>
/// Stage 3 input: captures a window with Windows Graphics Capture (falls back to GDI), masks password fields,
/// crops to the requested region and downscales – only this reduced image is ever sent to a vision model.
/// </summary>
public sealed class ScreenCaptureService : IScreenCapture
{
    private readonly KairoLogger _log;
    private IDirect3DDevice? _device;
    private bool _borderAccessRequested;

    public ScreenCaptureService(KairoLogger log) => _log = log;

    public bool PreferGraphicsCapture { get; set; } = true;

    /// <summary>Strategy used for the last capture ("WindowsGraphicsCapture" or "GDI").</summary>
    public string? LastStrategy { get; private set; }

    public async Task<CapturedImage?> CaptureWindowAsync(WindowInfo window, CaptureOptions options, CancellationToken cancellationToken)
    {
        RawImage? raw = null;
        if (PreferGraphicsCapture && IsGraphicsCaptureSupported())
        {
            try
            {
                raw = await CaptureWithGraphicsCaptureAsync(window, cancellationToken).ConfigureAwait(false);
                LastStrategy = "WindowsGraphicsCapture";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn("capture", $"Windows Graphics Capture failed: {ex.GetType().Name} – using GDI");
            }
        }
        if (raw is null)
        {
            raw = await Task.Run(() => CaptureWithGdi(window), cancellationToken).ConfigureAwait(false);
            LastStrategy = "GDI";
        }
        if (raw is null) { return null; }

        return await Task.Run(() => Process(raw, options), cancellationToken).ConfigureAwait(false);
    }

    private static bool IsGraphicsCaptureSupported()
    {
        try { return GraphicsCaptureSession.IsSupported(); }
        catch (Exception) { return false; }
    }

    // ------------------------------------------------------------------ Windows Graphics Capture
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow([In] nint window, [In] ref Guid iid);
        nint CreateForMonitor([In] nint monitor, [In] ref Guid iid);
    }

    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiDeviceIid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, out int featureLevel, out nint immediateContext);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    private IDirect3DDevice GetDevice()
    {
        if (_device is not null) { return _device; }
        const uint bgraSupport = 0x20;
        // Hardware first, WARP (software) as fallback for VMs and CI machines without GPU.
        var hr = D3D11CreateDevice(0, 1, 0, bgraSupport, 0, 0, 7, out var d3dDevice, out _, out var context);
        if (hr < 0) { hr = D3D11CreateDevice(0, 5, 0, bgraSupport, 0, 0, 7, out d3dDevice, out _, out context); }
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            var iid = DxgiDeviceIid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3dDevice, in iid, out var dxgiDevice));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable));
                try
                {
                    _device = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        finally
        {
            if (context != 0) { Marshal.Release(context); }
            Marshal.Release(d3dDevice);
        }
        return _device;
    }

    private static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        var pointer = interop.CreateForWindow(hwnd, ref iid);
        try
        {
            return GraphicsCaptureItem.FromAbi(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private async Task<RawImage> CaptureWithGraphicsCaptureAsync(WindowInfo window, CancellationToken cancellationToken)
    {
        var device = GetDevice();
        var item = CreateItemForWindow(window.Handle);
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        try { session.IsCursorCaptureEnabled = false; } catch (Exception) { }
        if (!_borderAccessRequested)
        {
            // Windows 11: ask once for borderless capture (no yellow frame around the window).
            _borderAccessRequested = true;
            try { await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask(cancellationToken).ConfigureAwait(false); }
            catch (Exception) { }
        }
        try { session.IsBorderRequired = false; } catch (Exception) { }

        var frameSource = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        pool.FrameArrived += (sender, _) =>
        {
            var frame = sender.TryGetNextFrame();
            if (frame is not null && !frameSource.TrySetResult(frame)) { frame.Dispose(); }
        };
        session.StartCapture();

        using var frame = await frameSource.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied).AsTask(cancellationToken).ConfigureAwait(false);
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var buffer = new global::Windows.Storage.Streams.Buffer((uint)(width * height * 4));
        bitmap.CopyToBuffer(buffer);
        var pixels = buffer.ToArray();

        var bounds = GetVisibleBounds(window.Handle);
        return new RawImage(pixels, width, height, new ScreenRect(bounds.Left, bounds.Top, width, height));
    }

    // ------------------------------------------------------------------ GDI fallback
    private RawImage? CaptureWithGdi(WindowInfo window)
    {
        var bounds = GetVisibleBounds(window.Handle);
        var width = bounds.Width;
        var height = bounds.Height;
        if (width <= 0 || height <= 0) { return null; }

        var screenDc = GetDC(0);
        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        var old = SelectObject(memDc, bitmap);
        try
        {
            if (!BitBlt(memDc, 0, 0, width, height, screenDc, bounds.Left, bounds.Top, SRCCOPY | CAPTUREBLT))
            {
                _log.Warn("capture", "BitBlt failed");
                return null;
            }
            SelectObject(memDc, old);
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };
            var pixels = new byte[width * height * 4];
            if (GetDIBits(memDc, bitmap, 0, (uint)height, pixels, ref header, 0) == 0) { return null; }
            for (var i = 3; i < pixels.Length; i += 4) { pixels[i] = 255; }
            return new RawImage(pixels, width, height, new ScreenRect(bounds.Left, bounds.Top, width, height));
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(0, screenDc);
        }
    }

    // ------------------------------------------------------------------ post processing
    private static CapturedImage Process(RawImage raw, CaptureOptions options)
    {
        var pixels = raw.Pixels;
        var width = raw.Width;
        var height = raw.Height;
        var region = raw.ScreenRegion;

        // Mask secrets (password fields) with solid black before anything else happens.
        foreach (var mask in options.MaskRegions)
        {
            var local = mask.Intersect(region);
            if (local.IsEmpty) { continue; }
            FillBlack(pixels, width, local.X - region.X, local.Y - region.Y, local.Width, local.Height);
        }

        BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        if (options.Region is { } wanted && !wanted.Intersect(region).IsEmpty)
        {
            var crop = wanted.Intersect(region);
            source = new CroppedBitmap(source, new System.Windows.Int32Rect(crop.X - region.X, crop.Y - region.Y, crop.Width, crop.Height));
            region = crop;
            width = crop.Width;
            height = crop.Height;
        }

        var scale = 1.0;
        var longest = Math.Max(width, height);
        if (longest > options.MaxEdge)
        {
            var factor = (double)options.MaxEdge / longest;
            source = new TransformedBitmap(source, new ScaleTransform(factor, factor));
            scale = 1 / factor;
        }
        source.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new CapturedImage(stream.ToArray(), "image/png", source.PixelWidth, source.PixelHeight, region, scale);
    }

    private static void FillBlack(byte[] pixels, int stride, int x, int y, int w, int h)
    {
        var height = pixels.Length / (stride * 4);
        for (var row = Math.Max(0, y); row < Math.Min(height, y + h); row++)
        {
            for (var col = Math.Max(0, x); col < Math.Min(stride, x + w); col++)
            {
                var i = (row * stride + col) * 4;
                pixels[i] = 0;
                pixels[i + 1] = 0;
                pixels[i + 2] = 0;
                pixels[i + 3] = 255;
            }
        }
    }
}
