// AppBackgroundTexture.axaml.cs
//
// Code-behind for AppBackgroundTexture.axaml.
//
// The glows are painted directly in Render()/DrawingContext rather than via
// Border.Background + RadialGradientBrush, because RadialGradientBrush's
// Center/RadiusX/RadiusY are relative to the bounding box of the geometry
// they're painted on (not absolute pixels) — see DrawGlow for details.
//
// The grain/noise layer is ALSO now drawn manually in Render(), instead of a
// plain XAML Border + ImageBrush + Opacity. Reason: the Android/Compose
// version uses BlendMode.Overlay, which blends noise INTO the underlying
// color (lightening/darkening relative to it) rather than laying a flat
// translucent gray layer on top. Avalonia 11.1 has no Visual.BlendMode API,
// so a plain Opacity-based ImageBrush washes the light-gray noise texture
// out over everything, including the indigo glow, making it unreadable.
// Instead we pre-bake an Overlay-blended version of the noise bitmap against
// the base background color once (in code), cache it, and paint that as a
// tiled brush — reproducing the "grain sits into the image" look without
// needing native blend-mode support.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DroidLens.Client.Views.Controls
{
    public partial class AppBackgroundTexture : UserControl
    {
        // ── Styled properties — mirror the @Composable fun's parameters ──────────

        public static readonly StyledProperty<Color> GlowColorProperty =
            AvaloniaProperty.Register<AppBackgroundTexture, Color>(
                nameof(GlowColor),
                defaultValue: Color.Parse("#818CF8")); // AccentIndigoColor fallback

        public static readonly StyledProperty<double> GlowAlphaProperty =
            AvaloniaProperty.Register<AppBackgroundTexture, double>(
                nameof(GlowAlpha), defaultValue: 0.24);

        public static readonly StyledProperty<double> BottomGlowAlphaProperty =
            AvaloniaProperty.Register<AppBackgroundTexture, double>(
                nameof(BottomGlowAlpha), defaultValue: 0.36);

        public static readonly StyledProperty<double> GrainAlphaProperty =
            AvaloniaProperty.Register<AppBackgroundTexture, double>(
                nameof(GrainAlpha), defaultValue: 0.045);

        // Background color the noise is blended against. Defaults to the app's
        // BgBaseColor (#0E0D14) — set this if you host the control over a
        // different backdrop so the overlay math stays accurate.
        public static readonly StyledProperty<Color> BackgroundTintColorProperty =
            AvaloniaProperty.Register<AppBackgroundTexture, Color>(
                nameof(BackgroundTintColor),
                defaultValue: Color.Parse("#0E0D14"));

        public Color GlowColor
        {
            get => GetValue(GlowColorProperty);
            set => SetValue(GlowColorProperty, value);
        }

        public double GlowAlpha
        {
            get => GetValue(GlowAlphaProperty);
            set => SetValue(GlowAlphaProperty, value);
        }

        public double BottomGlowAlpha
        {
            get => GetValue(BottomGlowAlphaProperty);
            set => SetValue(BottomGlowAlphaProperty, value);
        }

        public double GrainAlpha
        {
            get => GetValue(GrainAlphaProperty);
            set => SetValue(GrainAlphaProperty, value);
        }

        public Color BackgroundTintColor
        {
            get => GetValue(BackgroundTintColorProperty);
            set => SetValue(BackgroundTintColorProperty, value);
        }

        private WriteableBitmap? _overlayTintedNoise;
        private Color _bakedForColor;

        public AppBackgroundTexture()
        {
            AvaloniaXamlLoader.Load(this);

            this.GetObservable(GlowColorProperty).Subscribe(new AnonymousObserver<Color>(_ => InvalidateVisual()));
            this.GetObservable(GlowAlphaProperty).Subscribe(new AnonymousObserver<double>(_ => InvalidateVisual()));
            this.GetObservable(BottomGlowAlphaProperty).Subscribe(new AnonymousObserver<double>(_ => InvalidateVisual()));
            this.GetObservable(GrainAlphaProperty).Subscribe(new AnonymousObserver<double>(_ => InvalidateVisual()));
            this.GetObservable(BackgroundTintColorProperty).Subscribe(new AnonymousObserver<Color>(_ =>
            {
                _overlayTintedNoise = null; // force rebake against the new tint color
                InvalidateVisual();
            }));

            // Window is fixed-size (CanResize="False" in MainWindow.axaml), so this
            // control's own bounds never change after the first layout pass. We still
            // need one repaint once real bounds are available (first pass reports
            // 0x0), but there's no reason to redraw on every subsequent LayoutUpdated —
            // that fires for layout churn anywhere in the window (button hovers,
            // ScrollViewer content, etc.), not just changes relevant to us. Repainting
            // there would recreate the glow/grain brushes for no visible change.
            LayoutUpdated += (_, _) =>
            {
                if (!_hasPaintedOnce && Bounds.Width > 0 && Bounds.Height > 0)
                {
                    _hasPaintedOnce = true;
                    InvalidateVisual();
                }
            };
        }

        private bool _hasPaintedOnce;

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            var bounds = Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;

            // Top-right glow — compact accent in the corner, not a wash across the
            // whole window. Radius fraction dropped from 0.9 -> 0.4 so the glow fully
            // fades out well before reaching the window center (matches Android, where
            // the glow visibly dies out ~150-200px from its origin).
            // Top glow — moved from top-right (0.85) to top-left (0.15) so it lands
            // under the left column's semi-transparent cards instead of the opaque
            // black video preview panel on the right, which was fully hiding it.
            DrawGlow(context, bounds,
                centerFraction: new Point(0.15, 0.05),
                radiusFraction: 0.4,
                color: WithAlpha(GlowColor, GlowAlpha));

            // Bottom glow — moved from bottom-center (0.5) to sit under the left
            // column (~0.29 of the window width) for the same reason.
            DrawGlow(context, bounds,
                centerFraction: new Point(0.2, 1.0),
                radiusFraction: 0.35,
                color: WithAlpha(GlowColor, BottomGlowAlpha));

            DrawGrain(context, bounds);
        }

        private static void DrawGlow(DrawingContext context, Rect bounds, Point centerFraction, double radiusFraction, Color color)
        {
            var centerX = bounds.Width * centerFraction.X;
            var centerY = bounds.Height * centerFraction.Y;
            var radius = bounds.Width * radiusFraction;

            // CONFIRMED BUG (Avalonia issue #5947): when a gradient brush is painted via
            // DrawingContext.DrawRectangle() inside Render(), RelativeUnit.Relative values
            // for Center/RadiusX/RadiusY resolve against the *control's* Bounds — NOT
            // against the Rect passed into DrawRectangle. The previous code assumed the
            // latter (WPF-like) behavior, so Center=(0.5,0.5)/Radius=0.5 Relative was
            // actually being resolved against the full AppBackgroundTexture bounds, not
            // against glowRect. That put the real gradient center at the middle of the
            // whole control every time, regardless of centerFraction — glowRect only
            // clipped the visible area to a small window into that mis-centered gradient,
            // which is why the glow looked like a flat/washed-out tint instead of a glow,
            // and why increasing the radius made the color "spread across the whole
            // background" instead of growing a bigger visible glow at the corner.
            //
            // Fix: use Absolute units with real pixel coordinates for Center/Radius, and
            // draw across the full control bounds. Absolute values aren't affected by
            // which Rect is passed to DrawRectangle, so this sidesteps the bug entirely.
            var brush = new RadialGradientBrush
            {
                Center = new RelativePoint(centerX, centerY, RelativeUnit.Absolute),
                GradientOrigin = new RelativePoint(centerX, centerY, RelativeUnit.Absolute),
                RadiusX = new RelativeScalar(radius, RelativeUnit.Absolute),
                RadiusY = new RelativeScalar(radius, RelativeUnit.Absolute),
                GradientStops =
                {
                    new GradientStop { Color = color, Offset = 0 },
                    // Intermediate stop: keeps color dense through the first ~35% of the
                    // radius, then falls off steeply to transparent — a linear-only ramp
                    // (0 -> 1) spreads the same alpha thinly across the whole radius,
                    // which reads as a uniform gray wash instead of a glowing accent.
                    new GradientStop { Color = WithAlpha(color, color.A / 255.0 * 0.5), Offset = 0.35 },
                    new GradientStop { Color = WithAlpha(color, 0), Offset = 1 }
                }
            };

            context.DrawRectangle(brush, null, bounds);
        }

        private void DrawGrain(DrawingContext context, Rect bounds)
        {
            var tint = BackgroundTintColor;

            if (_overlayTintedNoise == null || _bakedForColor != tint)
            {
                _overlayTintedNoise = BuildOverlayTintedNoise(tint);
                _bakedForColor = tint;
            }

            if (_overlayTintedNoise == null)
                return;

            var size = _overlayTintedNoise.PixelSize;
            var brush = new ImageBrush(_overlayTintedNoise)
            {
                TileMode = TileMode.Tile,
                Stretch = Stretch.None,
                SourceRect = new RelativeRect(0, 0, size.Width, size.Height, RelativeUnit.Absolute),
                DestinationRect = new RelativeRect(0, 0, size.Width, size.Height, RelativeUnit.Absolute),
                Opacity = GrainAlpha
            };

            context.DrawRectangle(brush, null, bounds);
        }

        /// <summary>
        /// Loads noise_grain.png and pre-computes a standard Photoshop-style Overlay
        /// blend against <paramref name="baseColor"/>, baking the result into a new
        /// bitmap. Overlay formula per channel (0..1 range):
        ///   base &lt; 0.5 : 2 * base * blend
        ///   base &gt;= 0.5 : 1 - 2 * (1 - base) * (1 - blend)
        /// Alpha of the source noise pixel is preserved so GrainAlpha still controls
        /// overall strength via the brush's Opacity.
        /// </summary>
        private static WriteableBitmap? BuildOverlayTintedNoise(Color baseColor)
        {
            WriteableBitmap source;
            try
            {
                using var stream = AssetLoader.Open(new Uri("avares://DroidLens/Assets/Textures/noise_grain.png"));
                source = WriteableBitmap.Decode(stream);
            }
            catch
            {
                // Asset missing or inaccessible — skip the grain layer rather than crash.
                return null;
            }

            using (source)
            {
                var pixelSize = source.PixelSize;

                // WriteableBitmap.Decode() may not always produce Bgra8888 depending on
                // the source PNG — bail out rather than silently misreading byte order.
                if (source.Format != PixelFormat.Bgra8888)
                {
                    return null;
                }

                var result = new WriteableBitmap(pixelSize, source.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);

                var bR = baseColor.R / 255.0;
                var bG = baseColor.G / 255.0;
                var bB = baseColor.B / 255.0;

                using (var srcLock = source.Lock())
                using (var dstLock = result.Lock())
                {
                    unsafe
                    {
                        var srcPtr = (byte*)srcLock.Address;
                        var dstPtr = (byte*)dstLock.Address;

                        for (int y = 0; y < pixelSize.Height; y++)
                        {
                            var srcRow = srcPtr + y * srcLock.RowBytes;
                            var dstRow = dstPtr + y * dstLock.RowBytes;

                            for (int x = 0; x < pixelSize.Width; x++)
                            {
                                var i = x * 4;
                                // BGRA8888
                                byte blueB = srcRow[i + 0];
                                byte greenB = srcRow[i + 1];
                                byte redB = srcRow[i + 2];
                                byte alphaB = srcRow[i + 3];

                                double blendR = redB / 255.0;
                                double blendG = greenB / 255.0;
                                double blendBch = blueB / 255.0;

                                double outR = OverlayChannel(bR, blendR);
                                double outG = OverlayChannel(bG, blendG);
                                double outB = OverlayChannel(bB, blendBch);

                                dstRow[i + 0] = (byte)Math.Clamp(outB * 255.0, 0, 255);
                                dstRow[i + 1] = (byte)Math.Clamp(outG * 255.0, 0, 255);
                                dstRow[i + 2] = (byte)Math.Clamp(outR * 255.0, 0, 255);
                                dstRow[i + 3] = alphaB;
                            }
                        }
                    }
                }

                return result;
            }
        }

        private static double OverlayChannel(double baseC, double blendC)
        {
            return baseC < 0.5
                ? 2.0 * baseC * blendC
                : 1.0 - 2.0 * (1.0 - baseC) * (1.0 - blendC);
        }

        private static Color WithAlpha(Color color, double alpha)
        {
            var a = (byte)(Math.Clamp(alpha, 0.0, 1.0) * 255);
            return new Color(a, color.R, color.G, color.B);
        }

        // Minimal IObserver<T> wrapper so we don't need a System.Reactive dependency
        // just for these subscriptions.
        private sealed class AnonymousObserver<T> : IObserver<T>
        {
            private readonly Action<T> _onNext;
            public AnonymousObserver(Action<T> onNext) => _onNext = onNext;
            public void OnNext(T value) => _onNext(value);
            public void OnError(Exception error) { }
            public void OnCompleted() { }
        }
    }
}