using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using KvmSwitch.Core.Interfaces;
using Serilog;

namespace KvmSwitch.Infrastructure.Services
{
    public sealed class WindowsMonitorControlService : IMonitorControlService
    {
        private const byte VcpInputSourceCode = 0x60;
        private const byte VcpPowerModeCode = 0xD6;
        private const uint PowerOn = 0x01;
        private const uint PowerOff = 0x04;

        public Task SwitchInputAsync(int inputSourceId)
        {
            try
            {
                SwitchInput((uint)inputSourceId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to switch monitor input to {InputSource}.", inputSourceId);
                throw;
            }

            return Task.CompletedTask;
        }

        public Task TogglePowerAsync()
        {
            try
            {
                TogglePower();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to toggle monitor power.");
                throw;
            }

            return Task.CompletedTask;
        }

        private static void SwitchInput(uint inputSourceId)
        {
            var physicalMonitors = GetAllPhysicalMonitors();
            if (physicalMonitors.Count == 0)
            {
                Log.Warning("No physical monitors detected for input switching.");
                return;
            }

            try
            {
                foreach (var monitor in physicalMonitors)
                {
                    if (!SetVCPFeature(monitor.hPhysicalMonitor, VcpInputSourceCode, inputSourceId))
                    {
                        Log.Warning("Failed to set VCP input source on monitor {Description}.", monitor.szPhysicalMonitorDescription);
                    }
                }
            }
            finally
            {
                DestroyPhysicalMonitors((uint)physicalMonitors.Count, physicalMonitors.ToArray());
            }
        }

        private static void TogglePower()
        {
            var physicalMonitors = GetAllPhysicalMonitors();
            if (physicalMonitors.Count == 0)
            {
                Log.Warning("No physical monitors detected for power toggle.");
                return;
            }

            try
            {
                foreach (var monitor in physicalMonitors)
                {
                    uint currentValue = 0;
                    uint maxValue = 0;
                    if (!GetVCPFeatureAndVCPFeatureReply(monitor.hPhysicalMonitor, VcpPowerModeCode, ref currentValue, ref maxValue))
                    {
                        Log.Warning("Failed to read power state for monitor {Description}.", monitor.szPhysicalMonitorDescription);
                        continue;
                    }

                    var newValue = currentValue == PowerOn ? PowerOff : PowerOn;
                    if (!SetVCPFeature(monitor.hPhysicalMonitor, VcpPowerModeCode, newValue))
                    {
                        Log.Warning("Failed to set power state for monitor {Description}.", monitor.szPhysicalMonitorDescription);
                    }
                }
            }
            finally
            {
                DestroyPhysicalMonitors((uint)physicalMonitors.Count, physicalMonitors.ToArray());
            }
        }

        private static List<PHYSICAL_MONITOR> GetAllPhysicalMonitors()
        {
            var physicalMonitors = new List<PHYSICAL_MONITOR>();

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, MonitorEnumCallback, IntPtr.Zero);

            bool MonitorEnumCallback(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0)
                {
                    return true;
                }

                var monitors = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
                {
                    return true;
                }

                physicalMonitors.AddRange(monitors);
                return true;
            }

            return physicalMonitors;
        }

        private delegate bool MonitorEnumProc(
            IntPtr hMonitor,
            IntPtr hdcMonitor,
            ref RECT lprcMonitor,
            IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(
            IntPtr hdc,
            IntPtr lprcClip,
            MonitorEnumProc lpfnEnum,
            IntPtr dwData);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(
            IntPtr hMonitor,
            out uint pdwNumberOfPhysicalMonitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(
            IntPtr hMonitor,
            uint dwPhysicalMonitorArraySize,
            [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool DestroyPhysicalMonitors(
            uint dwPhysicalMonitorArraySize,
            [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVCPFeatureAndVCPFeatureReply(
            IntPtr hMonitor,
            byte bVCPCode,
            ref uint pdwCurrentValue,
            ref uint pdwMaximumValue);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool SetVCPFeature(
            IntPtr hMonitor,
            byte bVCPCode,
            uint dwNewValue);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }
    }
}
