using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Windows.Devices.Radios;

namespace Companion.Services;

public sealed class SystemControlService
{
    private const string PersonalizationKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int HwndBroadcast = 0xffff;
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    public bool IsDarkModeEnabled
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizationKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
    }

    public void SetDarkMode(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(PersonalizationKey)
            ?? throw new InvalidOperationException("Could not open the current user's personalization settings.");
        int lightThemeValue = enabled ? 0 : 1;
        key.SetValue("AppsUseLightTheme", lightThemeValue, RegistryValueKind.DWord);
        key.SetValue("SystemUsesLightTheme", lightThemeValue, RegistryValueKind.DWord);

        IntPtr settingName = Marshal.StringToHGlobalUni("ImmersiveColorSet");
        try
        {
            IntPtr broadcastResult;
            IntPtr sent = SendMessageTimeout(
                new IntPtr(HwndBroadcast),
                WmSettingChange,
                IntPtr.Zero,
                settingName,
                SmtoAbortIfHung,
                1000,
                out broadcastResult);
            if (sent == IntPtr.Zero)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(settingName);
        }
    }

    public (string DeviceName, int VolumePercent) GetDefaultAudioOutput()
    {
        using var enumerator = new MMDeviceEnumerator();
        using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return (device.FriendlyName, (int)Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100));
    }

    public void SetMasterVolumePercent(int percent)
    {
        using var enumerator = new MMDeviceEnumerator();
        using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f;
    }

    public bool IsMuted
    {
        get
        {
            using var enumerator = new MMDeviceEnumerator();
            using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.AudioEndpointVolume.Mute;
        }
    }

    public void SetMuted(bool muted)
    {
        using var enumerator = new MMDeviceEnumerator();
        using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        device.AudioEndpointVolume.Mute = muted;
    }

    private float? _preDuckVolume;
    private readonly object _duckLock = new();

    public void DuckAudio(float targetFactor = 0.20f)
    {
        lock (_duckLock)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (_preDuckVolume is null)
                {
                    _preDuckVolume = device.AudioEndpointVolume.MasterVolumeLevelScalar;
                    device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(_preDuckVolume.Value * targetFactor, 0.05f, 1.0f);
                }
            }
            catch
            {
            }
        }
    }

    public void UnduckAudio()
    {
        lock (_duckLock)
        {
            try
            {
                if (_preDuckVolume is not null)
                {
                    using var enumerator = new MMDeviceEnumerator();
                    using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    device.AudioEndpointVolume.MasterVolumeLevelScalar = _preDuckVolume.Value;
                    _preDuckVolume = null;
                }
            }
            catch
            {
            }
        }
    }

    public async Task SetBluetoothEnabledAsync(bool enabled)
    {
        Radio[] bluetoothRadios = await GetBluetoothRadiosAsync();
        if (bluetoothRadios.Length == 0)
        {
            throw new InvalidOperationException("No Bluetooth radio is available.");
        }

        RadioState target = enabled ? RadioState.On : RadioState.Off;
        foreach (Radio radio in bluetoothRadios)
        {
            RadioAccessStatus result = await radio.SetStateAsync(target);
            if (result != RadioAccessStatus.Allowed)
            {
                throw new UnauthorizedAccessException($"Windows did not allow changing Bluetooth state: {result}.");
            }
        }
    }

    public async Task<bool> GetBluetoothEnabledAsync()
    {
        Radio[] bluetoothRadios = await GetBluetoothRadiosAsync();
        if (bluetoothRadios.Length == 0)
        {
            throw new InvalidOperationException("No Bluetooth radio is available.");
        }

        return bluetoothRadios.Any(radio => radio.State == RadioState.On);
    }

    public bool GetWifiEnabled()
    {
        NetworkInterface? adapter = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(item => item.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
        return adapter?.OperationalStatus == OperationalStatus.Up;
    }

    public async Task SetWifiEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        NetworkInterface[] adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .OrderByDescending(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .ToArray();
        NetworkInterface? adapter = adapters.FirstOrDefault();
        if (adapter is null)
        {
            throw new InvalidOperationException("No Wi-Fi adapter is available.");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("netsh.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("interface");
        process.StartInfo.ArgumentList.Add("set");
        process.StartInfo.ArgumentList.Add("interface");
        process.StartInfo.ArgumentList.Add($"name={adapter.Name}");
        process.StartInfo.ArgumentList.Add(enabled ? "admin=enabled" : "admin=disabled");
        if (!process.Start())
        {
            throw new InvalidOperationException("Windows could not start netsh to change Wi-Fi state.");
        }

        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        string detail = string.Join(Environment.NewLine, await Task.WhenAll(output, error))
            .Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Windows could not change Wi-Fi adapter '{adapter.Name}' (exit {process.ExitCode}): {detail}");
        }
    }

    private static async Task<Radio[]> GetBluetoothRadiosAsync()
    {
        IReadOnlyList<Radio> radios = await Radio.GetRadiosAsync();
        return radios.Where(radio => radio.Kind == RadioKind.Bluetooth).ToArray();
    }
}
