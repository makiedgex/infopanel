using InfoPanel.CorsairPanel;
using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Utils;
using Serilog;
using SkiaSharp;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Services
{
    public sealed class CorsairPanelDeviceTask : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<CorsairPanelDeviceTask>();

        // The pump LCD tops out at ~30 FPS; never ask for more than that.
        private const int MAX_FRAME_RATE = 30;

        private readonly CorsairPanelDevice _device;
        private int _panelWidth;
        private int _panelHeight;

        public CorsairPanelDeviceTask(CorsairPanelDevice device)
        {
            _device = device;
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            var modelInfo = _device.ModelInfo;
            if (modelInfo == null)
            {
                _device.UpdateRuntimeProperties(errorMessage: "Unknown model");
                return;
            }

            _panelWidth = modelInfo.Width;
            _panelHeight = modelInfo.Height;

            _device.UpdateRuntimeProperties(isRunning: false, errorMessage: string.Empty);
            _device.RuntimeProperties.Name = $"{modelInfo.Name} ({_panelWidth}x{_panelHeight})";

            int retryCount = 0;
            while (!token.IsCancellationRequested)
            {
                CorsairHidDevice? hidDevice = null;
                try
                {
                    Logger.Information("CorsairDevice {Device}: Opening (attempt {Retry})", _device, retryCount + 1);
                    hidDevice = CorsairHidDevice.Open(modelInfo.VendorId, modelInfo.ProductId,
                        CorsairPanelHelper.SerialFromDeviceId(_device.DeviceId));

                    if (hidDevice == null)
                    {
                        _device.UpdateRuntimeProperties(errorMessage: "Device not found or in use by another program (close iCUE / HydroScreen)");
                        await Task.Delay(retryCount < 3 ? 1000 : 5000, token);
                        retryCount++;
                        continue;
                    }

                    retryCount = 0;
                    await RunRenderSendLoop(hidDevice, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "CorsairDevice {Device}: Error", _device);
                    _device.UpdateRuntimeProperties(errorMessage: ex.Message);
                    retryCount++;
                }
                finally
                {
                    hidDevice?.Dispose();
                    _device.UpdateRuntimeProperties(isRunning: false);
                }

                if (!token.IsCancellationRequested)
                    await Task.Delay(retryCount < 3 ? 1000 : 5000, token);
            }
        }

        private async Task RunRenderSendLoop(CorsairHidDevice hidDevice, CancellationToken token)
        {
            FpsCounter fpsCounter = new(60);
            byte[]? latestFrame = null;
            AutoResetEvent frameAvailable = new(false);

            var renderCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var renderToken = renderCts.Token;

            int lastBrightness = _device.Brightness;
            hidDevice.SetBrightness(lastBrightness);

            _device.UpdateRuntimeProperties(isRunning: true, errorMessage: string.Empty);

            var renderTask = Task.Run(async () =>
            {
                Thread.CurrentThread.Name ??= $"Corsair-Render-{_device}";
                try
                {
                    var stopwatch = new Stopwatch();
                    while (!renderToken.IsCancellationRequested)
                    {
                        stopwatch.Restart();

                        var frame = GenerateJpegBuffer();
                        Interlocked.Exchange(ref latestFrame, frame);
                        frameAvailable.Set();

                        var targetFrameTime = 1000 / Math.Clamp(_device.TargetFrameRate, 1, MAX_FRAME_RATE);
                        var remaining = targetFrameTime - (int)stopwatch.ElapsedMilliseconds;
                        if (remaining > 0)
                            await Task.Delay(remaining, renderToken);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception e)
                {
                    Logger.Error(e, "CorsairDevice {Device}: Render error", _device);
                    _device.UpdateRuntimeProperties(errorMessage: e.Message);
                    renderCts.Cancel();
                }
            }, renderToken);

            var sendTask = Task.Run(() =>
            {
                Thread.CurrentThread.Name ??= $"Corsair-Send-{_device}";
                try
                {
                    var stopwatch = new Stopwatch();
                    while (!renderToken.IsCancellationRequested)
                    {
                        if (lastBrightness != _device.Brightness)
                        {
                            lastBrightness = _device.Brightness;
                            hidDevice.SetBrightness(lastBrightness);
                        }

                        if (frameAvailable.WaitOne(100))
                        {
                            var jpegData = Interlocked.Exchange(ref latestFrame, null);
                            if (jpegData != null)
                            {
                                stopwatch.Restart();

                                hidDevice.SendJpegFrame(jpegData);

                                fpsCounter.Update(stopwatch.ElapsedMilliseconds);
                                _device.UpdateRuntimeProperties(frameRate: fpsCounter.FramesPerSecond, frameTime: fpsCounter.FrameTime);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    // Typically the cap was unplugged or taken by another program; the outer loop reconnects.
                    Logger.Error(e, "CorsairDevice {Device}: Send error", _device);
                    _device.UpdateRuntimeProperties(errorMessage: e.Message);
                }
                finally
                {
                    renderCts.Cancel();
                }
            }, renderToken);

            await Task.WhenAll(renderTask, sendTask);

            frameAvailable.Dispose();
            renderCts.Dispose();
        }

        private byte[] GenerateJpegBuffer()
        {
            var profileGuid = _device.ProfileGuid;

            if (ConfigModel.Instance.GetProfile(profileGuid) is Profile profile)
            {
                var rotation = _device.Rotation;

                using var bitmap = PanelDrawTask.RenderSK(profile, false,
                    colorType: SKColorType.Rgba8888,
                    alphaType: SKAlphaType.Opaque);

                using var resizedBitmap = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, rotation);

                using var image = SKImage.FromBitmap(resizedBitmap);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(_device.JpegQuality, 50, 100));
                return data.ToArray();
            }

            // No profile selected: return a black JPEG
            return GenerateBlackJpeg();
        }

        private byte[]? _cachedBlackJpeg;

        private byte[] GenerateBlackJpeg()
        {
            if (_cachedBlackJpeg != null) return _cachedBlackJpeg;

            using var bitmap = new SKBitmap(_panelWidth, _panelHeight, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Black);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 50);
            _cachedBlackJpeg = data.ToArray();
            return _cachedBlackJpeg;
        }
    }
}
