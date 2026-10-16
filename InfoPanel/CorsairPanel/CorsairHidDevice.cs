using HidSharp;
using Serilog;
using System;
using System.Linq;

namespace InfoPanel.CorsairPanel
{
    /// <summary>
    /// HID communication for Corsair pump LCD caps (VID 1B1C, "PumpDisplay" protocol).
    /// Verified on hardware with the Nautilus LCD Cap (PID 0C57).
    ///
    /// Image   : 1024-byte output reports, report ID 0x02.
    ///           [02][05][40][is_end][part lo][part hi][len lo][len hi] + up to 1016 bytes of JPEG
    /// Command : 32-byte feature reports, report ID 0x03, payload [cmd, value]
    ///           cmd 0x0B = brightness (0x01 / 0x04 / 0x10 / 0x40), 0x0C = rotation, 0x19 = persist
    /// </summary>
    public class CorsairHidDevice : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<CorsairHidDevice>();

        public const int OUTPUT_REPORT_SIZE = 1024;   // includes the report ID byte
        public const int FEATURE_REPORT_SIZE = 32;    // includes the report ID byte
        private const int IMAGE_HEADER_SIZE = 8;
        private const int IMAGE_PAYLOAD_PER_REPORT = OUTPUT_REPORT_SIZE - IMAGE_HEADER_SIZE; // 1016

        private const byte IMAGE_REPORT_ID = 0x02;
        private const byte FEATURE_REPORT_ID = 0x03;

        private HidStream? _stream;
        private bool _disposed;
        private readonly object _lock = new();
        private readonly byte[] _packet = new byte[OUTPUT_REPORT_SIZE];

        public bool IsOpen => _stream != null;

        /// <summary>
        /// Opens the Corsair LCD HID interface by VID/PID. When <paramref name="serialNumber"/> is given and the
        /// device reports one, only the device with that serial is opened (several caps on one PC).
        /// </summary>
        public static CorsairHidDevice? Open(int vendorId, int productId, string? serialNumber = null)
        {
            var hidDevices = DeviceList.Local.GetHidDevices(vendorId, productId).ToList();

            if (hidDevices.Count == 0)
            {
                Logger.Warning("CorsairHidDevice: No HID device found for VID={Vid:X4} PID={Pid:X4}", vendorId, productId);
                return null;
            }

            foreach (var device in hidDevices)
            {
                try
                {
                    if (device.GetMaxOutputReportLength() < OUTPUT_REPORT_SIZE)
                    {
                        Logger.Debug("CorsairHidDevice: Skipping {Path}, MaxOut={MaxOut}", device.DevicePath, device.GetMaxOutputReportLength());
                        continue;
                    }

                    if (!string.IsNullOrEmpty(serialNumber))
                    {
                        var serial = CorsairPanelHelper.TryGetSerial(device);
                        if (!string.IsNullOrEmpty(serial) && !serial.Equals(serialNumber, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }

                    var stream = device.Open();
                    stream.WriteTimeout = 2000;
                    stream.ReadTimeout = 500;

                    Logger.Information("CorsairHidDevice: Opened {Path}", device.DevicePath);
                    return new CorsairHidDevice { _stream = stream };
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "CorsairHidDevice: Failed to open {Path}", device.DevicePath);
                }
            }

            Logger.Error("CorsairHidDevice: Could not open any matching device (is iCUE or HydroScreen using it?)");
            return null;
        }

        /// <summary>Sets the hardware brightness (0-100, quantized to the 4 levels the hardware supports).</summary>
        public void SetBrightness(int percent)
        {
            byte raw = percent switch
            {
                <= 16 => 0x01,
                <= 49 => 0x04,
                <= 83 => 0x10,
                _ => 0x40,
            };

            SendFeature(0x0B, raw);
        }

        /// <summary>Sends one JPEG frame (480x480).</summary>
        public void SendJpegFrame(byte[] jpegData)
        {
            lock (_lock)
            {
                if (_stream == null) throw new InvalidOperationException("Device not open");

                int part = 0;
                for (int offset = 0; offset < jpegData.Length; offset += IMAGE_PAYLOAD_PER_REPORT, part++)
                {
                    int len = Math.Min(IMAGE_PAYLOAD_PER_REPORT, jpegData.Length - offset);
                    bool isEnd = offset + len >= jpegData.Length;

                    Array.Clear(_packet, 0, _packet.Length);
                    _packet[0] = IMAGE_REPORT_ID;
                    _packet[1] = 0x05;
                    _packet[2] = 0x40;
                    _packet[3] = (byte)(isEnd ? 1 : 0);
                    _packet[4] = (byte)(part & 0xFF);
                    _packet[5] = (byte)((part >> 8) & 0xFF);
                    _packet[6] = (byte)(len & 0xFF);
                    _packet[7] = (byte)((len >> 8) & 0xFF);
                    Array.Copy(jpegData, offset, _packet, IMAGE_HEADER_SIZE, len);

                    _stream.Write(_packet, 0, _packet.Length);
                }
            }
        }

        private void SendFeature(byte command, byte value)
        {
            lock (_lock)
            {
                if (_stream == null) throw new InvalidOperationException("Device not open");

                var buffer = new byte[FEATURE_REPORT_SIZE];
                buffer[0] = FEATURE_REPORT_ID;
                buffer[1] = command;
                buffer[2] = value;
                _stream.SetFeature(buffer);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _stream?.Dispose();
                _stream = null;
            }
        }
    }
}
