using System.Runtime.InteropServices;
using System.Threading;
using DroidLens.Client;

namespace DroidLens.Client.VirtualCamera;

public sealed class VirtualCameraService : IDisposable
{
    // P/Invoke до softcam.dll
    [DllImport("softcam.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr scCreateCamera(int width, int height, float fps);

    [DllImport("softcam.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void scDeleteCamera(IntPtr camera);

    [DllImport("softcam.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void scSendFrame(IntPtr camera, byte[] pixels);

    // ── State ──────────────────────────────────────────────────────────────
    private IntPtr _camera = IntPtr.Zero;
    private int _width, _height;

    public bool IsRunning => _camera != IntPtr.Zero;
    public bool IsAvailable => VirtualCameraInstaller.IsInstalled();
    public bool MirrorHorizontal { get; set; } = false;

    // ── Start ──────────────────────────────────────────────────────────────
    public bool Start(int width = 1280, int height = 720, float fps = 30f)
    {
        if (!IsAvailable)
        {
            AppLog.W("VCam", () => "softcam.dll не встановлена");
            return false;
        }

        Stop();

        return TryCreateCamera(width, height, fps);
    }

    // scCreateCamera can transiently return null right after scDeleteCamera —
    // the DirectShow filter/COM object it tears down isn't guaranteed to be
    // fully released by the time scDeleteCamera returns (other consumers like
    // OBS/browsers may still hold a reference, or softcam.dll's own internal
    // cleanup runs on another thread). This only ever mattered once resolution
    // could change mid-stream (PushFrame/PushJpegFrame's Stop()+Start() below) —
    // a fixed 720p target never hit this path before. A short delay + a couple
    // retries covers the normal case; if it still fails the caller (PushFrame)
    // just skips this frame and tries again on the next one.
    private bool TryCreateCamera(int width, int height, float fps, int attempt = 0)
    {
        try
        {
            _camera = scCreateCamera(width, height, fps);
            if (_camera == IntPtr.Zero)
            {
                if (attempt < 3)
                {
                    AppLog.W("VCam", () => $"scCreateCamera повернула null, retry {attempt + 1}/3");
                    Thread.Sleep(150);
                    return TryCreateCamera(width, height, fps, attempt + 1);
                }
                AppLog.E("VCam", () => "scCreateCamera повернула null (усі спроби вичерпано)");
                return false;
            }

            _width  = width;
            _height = height;
            _lastFailedRestartUtc = DateTime.MinValue;
            AppLog.I("VCam", () => $"Started {width}x{height}@{fps}fps");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.E("VCam", ex, () => "Start FAILED");
            return false;
        }
    }

    // ── Stop ───────────────────────────────────────────────────────────────
    public void Stop()
    {
        _lastFailedRestartUtc = DateTime.MinValue;
        if (_camera == IntPtr.Zero) return;
        scDeleteCamera(_camera);
        _camera = IntPtr.Zero;
        AppLog.I("VCam", () => "Stopped");
    }

    // Guards PushFrame/PushJpegFrame's resize-restart against being retried
    // every single incoming frame while scCreateCamera keeps failing (e.g.
    // some external consumer still has the old pin open) — without this,
    // every frame at 30fps would hammer Stop()+Start() in a tight loop.
    private DateTime _lastFailedRestartUtc = DateTime.MinValue;
    private static readonly TimeSpan RestartRetryCooldown = TimeSpan.FromSeconds(2);

    // ── Push BGRA frame з VideoDecoder ─────────────────────────────────────
    // rotation: in practice only 0 or 180 (CommandServer.kt's stream_status
    // only ever sends rotation = isReverseLandscape ? 180 : 0 — H264/H265
    // streaming is landscape-only, no 90/270 case exists on the wire). VCam
    // never actually rotated the frame before — H.264/H.265 preview only
    // looked correct because PreviewArea applied a UI-side RenderTransform on
    // top of the raw (unrotated) bgra buffer, and that transform never
    // touched what got pushed here. Rotating before BgraToRgb24 keeps VCam's
    // output identical to what the on-screen preview shows. RotateBgra below
    // also handles 90/270 for completeness/future-proofing, even though only
    // 0/180 are reachable today.
    public void PushFrame(byte[] bgra, int width, int height, int rotation = 0)
    {
        if (!IsRunning) return;

        var (rotatedBgra, outWidth, outHeight) = RotateBgra(bgra, width, height, rotation);

        if (outWidth != _width || outHeight != _height)
        {
            if (DateTime.UtcNow - _lastFailedRestartUtc < RestartRetryCooldown) return;

            Stop();
            if (!Start(outWidth, outHeight))
            {
                _lastFailedRestartUtc = DateTime.UtcNow;
                return;
            }
        }

        byte[] rgb = BgraToRgb24(rotatedBgra, outWidth, outHeight);
        rgb = FlipRgb24Horizontal(rgb, outWidth, outHeight);
        scSendFrame(_camera, rgb);
    }

    // Rotates a BGRA32 buffer by 0/90/180/270 degrees clockwise. 90/270 swap
    // width and height in the returned tuple — callers must use those (not
    // the original width/height) for anything downstream, including the
    // camera-resize check above.
    private static unsafe (byte[] bgra, int width, int height) RotateBgra(
        byte[] bgra, int width, int height, int rotation)
    {
        switch (((rotation % 360) + 360) % 360)
        {
            case 90:
            {
                var rotated = new byte[bgra.Length];
                fixed (byte* src = bgra, dst = rotated)
                {
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int dstX = height - 1 - y;
                            int dstY = x;
                            *(int*)(dst + (dstY * height + dstX) * 4) = *(int*)(src + (y * width + x) * 4);
                        }
                    }
                }
                return (rotated, height, width);
            }
            case 180:
            {
                var rotated = new byte[bgra.Length];
                int total = width * height;
                fixed (byte* src = bgra, dst = rotated)
                {
                    int* s = (int*)src;
                    int* d = (int*)dst;
                    for (int i = 0; i < total; i++)
                        d[total - 1 - i] = s[i];
                }
                return (rotated, width, height);
            }
            case 270:
            {
                var rotated = new byte[bgra.Length];
                fixed (byte* src = bgra, dst = rotated)
                {
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int dstX = y;
                            int dstY = width - 1 - x;
                            *(int*)(dst + (dstY * height + dstX) * 4) = *(int*)(src + (y * width + x) * 4);
                        }
                    }
                }
                return (rotated, height, width);
            }
            default: // 0
                return (bgra, width, height);
        }
    }
    
    public void PushJpegFrame(byte[] jpeg)
    {
        if (!IsRunning) return;
        try
        {
            using var ms  = new System.IO.MemoryStream(jpeg);
            using var bmp = System.Drawing.Bitmap.FromStream(ms) as System.Drawing.Bitmap;
            if (bmp == null) return;

            int w = bmp.Width, h = bmp.Height;

            if (w != _width || h != _height)
            {
                if (DateTime.UtcNow - _lastFailedRestartUtc < RestartRetryCooldown) return;

                Stop();
                if (!Start(w, h))
                {
                    _lastFailedRestartUtc = DateTime.UtcNow;
                    return;
                }
            }

            var rect    = new System.Drawing.Rectangle(0, 0, w, h);
            var bmpData = bmp.LockBits(rect,
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            byte[] bgra = new byte[w * h * 4];
            Marshal.Copy(bmpData.Scan0, bgra, 0, bgra.Length);
            bmp.UnlockBits(bmpData);

            byte[] rgb = ArgbToRgb24(bgra, w, h);
            scSendFrame(_camera, rgb);
        }
        catch (Exception ex)
        {
            AppLog.E("VCam", ex, () => "PushJpegFrame error");
        }
    }
    
    // ARGB (GDI+ Format32bppArgb, stored as BGRA in memory) → BGR24 for softcam.dll
    private static unsafe byte[] ArgbToRgb24(byte[] argb, int width, int height)
    {
        byte[] bgr = new byte[width * height * 3];
        fixed (byte* src = argb, dst = bgr)
        {
            byte* s = src, d = dst;
            int total = width * height;
            for (int i = 0; i < total; i++)
            {
                *d++ = *s++;  // B
                *d++ = *s++;  // G
                *d++ = *s++;  // R
                s++;          // skip A
            }
        }
        return bgr;
    }

    // Horizontal flip of RGB24 buffer
    private static unsafe byte[] FlipRgb24Horizontal(byte[] rgb, int width, int height)
    {
        byte[] flipped = new byte[rgb.Length];
        fixed (byte* src = rgb, dst = flipped)
        {
            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    byte* s = src + (row * width + col) * 3;
                    byte* d = dst + (row * width + (width - 1 - col)) * 3;
                    d[0] = s[0];
                    d[1] = s[1];
                    d[2] = s[2];
                }
            }
        }
        return flipped;
    }

    // RGBA (from FFmpeg) → BGR24 for softcam.dll
    private static unsafe byte[] BgraToRgb24(byte[] rgba, int width, int height)
    {
        byte[] bgr = new byte[width * height * 3];
        fixed (byte* src = rgba, dst = bgr)
        {
            byte* s = src, d = dst;
            int total = width * height;
            for (int i = 0; i < total; i++)
            {
                *d++ = *s++;  // R
                *d++ = *s++;  // G
                *d++ = *s++;  // B
                s++;          // skip A
            }
        }
        return bgr;
    }

    public void Dispose() => Stop();
}