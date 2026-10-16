using HidSharp;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.CorsairPanel
{
    public class CorsairPanelDiscoveryInfo
    {
        public string DeviceId { get; set; } = "";
        public string DeviceLocation { get; set; } = "";
        public string DevicePath { get; set; } = "";
        public int VendorId { get; set; }
        public int ProductId { get; set; }
        public CorsairPanelModel Model { get; set; }
        public CorsairPanelModelInfo? ModelInfo { get; set; }
    }

    public static class CorsairPanelHelper
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(CorsairPanelHelper));

        /// <summary>Scans for connected Corsair LCD caps via HidSharp.</summary>
        public static List<CorsairPanelDiscoveryInfo> ScanDevices()
        {
            var devices = new List<CorsairPanelDiscoveryInfo>();

            foreach (var (vendorId, productId) in CorsairPanelModelDatabase.SupportedDevices)
            {
                Logger.Information("CorsairPanelHelper: Scanning VID={Vid:X4} PID={Pid:X4}", vendorId, productId);

                foreach (var hidDevice in DeviceList.Local.GetHidDevices(vendorId, productId).ToList())
                {
                    try
                    {
                        // The cap exposes several HID collections; only the one with 1024-byte output reports is the display.
                        if (hidDevice.GetMaxOutputReportLength() < CorsairHidDevice.OUTPUT_REPORT_SIZE)
                        {
                            Logger.Debug("CorsairPanelHelper: Skipping {Path}, MaxOut={MaxOut}",
                                hidDevice.DevicePath, hidDevice.GetMaxOutputReportLength());
                            continue;
                        }

                        var modelInfo = CorsairPanelModelDatabase.GetModelByVidPid(vendorId, productId);
                        if (modelInfo == null) continue;

                        var serial = TryGetSerial(hidDevice);
                        var (pathId, pathLocation) = ParsePath(hidDevice.DevicePath);

                        // Prefer the serial number as a stable identity; fall back to the HID instance id.
                        string deviceId = string.IsNullOrEmpty(serial)
                            ? pathId
                            : $"VID_{vendorId:X4}&PID_{productId:X4}\\{serial}";

                        Logger.Information("CorsairPanelHelper: Found {Model} at {Path}", modelInfo.Name, hidDevice.DevicePath);

                        devices.Add(new CorsairPanelDiscoveryInfo
                        {
                            DeviceId = deviceId,
                            DeviceLocation = pathLocation,
                            DevicePath = hidDevice.DevicePath,
                            VendorId = vendorId,
                            ProductId = productId,
                            Model = modelInfo.Model,
                            ModelInfo = modelInfo,
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning(ex, "CorsairPanelHelper: Error scanning {Path}", hidDevice.DevicePath);
                    }
                }
            }

            Logger.Information("CorsairPanelHelper: Found {Count} device(s)", devices.Count);
            return devices;
        }

        /// <summary>Returns the USB serial number, or null if the device does not expose it.</summary>
        public static string? TryGetSerial(HidDevice device)
        {
            try
            {
                var serial = device.GetSerialNumber();
                return string.IsNullOrWhiteSpace(serial) ? null : serial.Trim();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Extracts the serial portion of a DeviceId ("VID_xxxx&amp;PID_xxxx\serial"), or null.</summary>
        public static string? SerialFromDeviceId(string deviceId)
        {
            var idx = deviceId.IndexOf('\\');
            return idx >= 0 && idx < deviceId.Length - 1 ? deviceId[(idx + 1)..] : null;
        }

        // HID path: \\?\hid#vid_1b1c&pid_0c57#6&306ad717&0&0000#{guid}
        private static (string DeviceId, string Location) ParsePath(string devicePath)
        {
            try
            {
                var parts = devicePath.Split('#');
                if (parts.Length >= 3)
                    return ($"{parts[1]}\\{parts[2]}", parts[2]);
            }
            catch { }

            return (devicePath, "");
        }
    }
}
