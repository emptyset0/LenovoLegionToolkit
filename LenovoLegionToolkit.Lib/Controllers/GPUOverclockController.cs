using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Listeners;
using LenovoLegionToolkit.Lib.Settings;
using LenovoLegionToolkit.Lib.SoftwareDisabler;
using LenovoLegionToolkit.Lib.System;
using LenovoLegionToolkit.Lib.System.Management;
using LenovoLegionToolkit.Lib.Utils;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;

namespace LenovoLegionToolkit.Lib.Controllers;

public class GPUOverclockController
{
    private const int DefaultMaxCoreDeltaMhz = 500;
    private const int DefaultMaxMemoryDeltaMhz = 2000;
    private const int NvidiaGraphicsClockId = 0;
    private const int NvidiaMemoryClockId = 4;
    private const string YogaPro14sMachineType = "83BU";
    private static readonly GPUOverclockInfo YogaPro14sDefaultDeltaMhz = new(150, 300);
    private static readonly GPUOverclockInfo YogaPro14sMaxDeltaMhz = new(200, 400);

    private readonly GPUOverclockSettings _settings;
    private readonly VantageDisabler _vantageDisabler;
    private readonly LegionSpaceDisabler _legionSpaceDisabler;
    private readonly LegionZoneDisabler _legionZoneDisabler;
    private readonly NativeWindowsMessageListener _nativeWindowsMessageListener;

    public event EventHandler? Changed;

    public GPUOverclockController(GPUOverclockSettings settings,
        VantageDisabler vantageDisabler,
        LegionSpaceDisabler legionSpaceDisabler,
        LegionZoneDisabler legionZoneDisabler,
        NativeWindowsMessageListener nativeWindowsMessageListener)
    {
        _settings = settings;
        _vantageDisabler = vantageDisabler;
        _legionSpaceDisabler = legionSpaceDisabler;
        _legionZoneDisabler = legionZoneDisabler;
        _nativeWindowsMessageListener = nativeWindowsMessageListener;
        _nativeWindowsMessageListener.Changed += NativeWindowsMessageListenerOnChanged;
    }

    public async Task<bool> IsSupportedAsync()
    {
        bool isSupported;
        PhysicalGPU? gpu = null;

        try
        {
            if (AppFlags.Instance.Debug)
            {
                return true;
            }

            NVAPI.Initialize();
            gpu = NVAPI.GetGPU();
            isSupported = gpu is not null;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"NVAPI status check failed.", ex);
            isSupported = false;
        }

        Log.Instance.Trace($"NVAPI status: {isSupported}. [gpu={gpu?.FullName ?? "null"}]");

        if (!isSupported)
            return isSupported;

        try
        {
            var supportGpuOc = await WMI.LenovoGameZoneData.IsSupportGpuOCAsync().ConfigureAwait(false);
            isSupported = supportGpuOc > 0;
            Log.Instance.Trace($"IsSupportGpuOC returned {supportGpuOc}.");
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"GPU OC support query failed.", ex);
            isSupported = false;
        }

        if (!isSupported)
            isSupported = await IsYogaPro14s83BUGpuOcFallbackSupportedAsync().ConfigureAwait(false);

        if (!isSupported)
        {
            Log.Instance.Trace($"Clearing settings...");

            _settings.Store.Enabled = false;
            _settings.Store.Info = GPUOverclockInfo.Zero;
            _settings.SynchronizeStore();
        }
        else
        {
            var maxDeltaMhz = await GetMaxDeltaMhzAsync().ConfigureAwait(false);
            Log.Instance.Trace($"GPU OC max delta: {maxDeltaMhz}.");
        }

        Log.Instance.Trace($"Supports GPU OC status: {isSupported}");

        return isSupported;
    }

    public (bool, GPUOverclockInfo) GetState() => (_settings.Store.Enabled, _settings.Store.Info);

    public void SaveState(bool enabled, GPUOverclockInfo info)
    {
        _settings.Store.Enabled = enabled;
        _settings.Store.Info = info;
        _settings.SynchronizeStore();
    }

    public async Task ApplyStateAsync(bool force = false)
    {
        if (await _vantageDisabler.GetStatusAsync().ConfigureAwait(false) == SoftwareStatus.Enabled)
        {
            Log.Instance.Trace($"Can't correctly apply state when Vantage is running.");

            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (await _legionSpaceDisabler.GetStatusAsync().ConfigureAwait(false) == SoftwareStatus.Enabled)
        {
            Log.Instance.Trace($"Can't correctly apply state when Legion Space is running.");

            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (await _legionZoneDisabler.GetStatusAsync().ConfigureAwait(false) == SoftwareStatus.Enabled)
        {
            Log.Instance.Trace($"Can't correctly apply state when Legion Zone is running.");

            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var enabled = _settings.Store.Enabled;
        var info = _settings.Store.Info;

        if (force)
        {
            info = enabled ? info : GPUOverclockInfo.Zero;
            enabled = true;

            Log.Instance.Trace($"Forcing... [enabled=true, info={info}]");
        }

        if (enabled && info == GPUOverclockInfo.Zero)
        {
            var defaultDeltaMhz = await GetDefaultDeltaMhzAsync().ConfigureAwait(false);
            if (defaultDeltaMhz != GPUOverclockInfo.Zero)
            {
                info = defaultDeltaMhz;
                _settings.Store.Info = info;
                _settings.SynchronizeStore();
                Log.Instance.Trace($"Using default overclock info: {info}.");
            }
        }

        if (!enabled)
        {
            Log.Instance.Trace($"Not enabled.");

            Changed?.Invoke(this, EventArgs.Empty);

            return;
        }

        Log.Instance.Trace($"Applying overclock: {info}.");

        try
        {
            NVAPI.Initialize();

            var gpu = NVAPI.GetGPU();
            if (gpu is null)
            {
                Log.Instance.Trace($"dGPU not found.");

                Changed?.Invoke(this, EventArgs.Empty);

                return;
            }

            var maxDeltaMhz = await GetMaxDeltaMhzAsync().ConfigureAwait(false);
            SetOverclockInfo(gpu, info, maxDeltaMhz);

            Log.Instance.Trace($"Applied overclock: {info}, max: {maxDeltaMhz}, current: {GetOverclockInfo(gpu)}.");
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to apply overclock: {info}, clearing settings...", ex);

            _settings.Store.Enabled = false;
            _settings.Store.Info = GPUOverclockInfo.Zero;
            _settings.SynchronizeStore();
        }
        finally
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<bool> EnsureOverclockIsAppliedAsync()
    {
        var (enabled, _) = GetState();
        if (!enabled)
            return false;

        await ApplyStateAsync().ConfigureAwait(false);
        return true;
    }

    private async void NativeWindowsMessageListenerOnChanged(object? sender, NativeWindowsMessageListener.ChangedEventArgs e)
    {
        if (e.Message != NativeWindowsMessage.OnDisplayDeviceArrival)
            return;

        if (await IsSupportedAsync().ConfigureAwait(false))
            await ApplyStateAsync().ConfigureAwait(false);
    }

    public static int GetMaxCoreDeltaMhz() => DefaultMaxCoreDeltaMhz;

    public static int GetMaxMemoryDeltaMhz() => DefaultMaxMemoryDeltaMhz;

    public static async Task<GPUOverclockInfo> GetDefaultDeltaMhzAsync()
    {
        return await IsYogaPro14s83BUAsync().ConfigureAwait(false)
            ? YogaPro14sDefaultDeltaMhz
            : GPUOverclockInfo.Zero;
    }

    public static async Task<GPUOverclockInfo> GetMaxDeltaMhzAsync()
    {
        if (await IsYogaPro14s83BUAsync().ConfigureAwait(false))
        {
            Log.Instance.Trace($"Using YogaPro 14s 83BU GPU OC max delta: {YogaPro14sMaxDeltaMhz}.");
            return YogaPro14sMaxDeltaMhz;
        }

        var defaultMax = new GPUOverclockInfo(DefaultMaxCoreDeltaMhz, DefaultMaxMemoryDeltaMhz);

        var capabilities = await ReadGpuOverclockCapabilitiesAsync().ConfigureAwait(false);
        if (capabilities.Length == 0)
        {
            Log.Instance.Trace($"GPU OC capability data is empty. Using defaults.");
            return defaultMax;
        }

        Log.Instance.Trace($"GPU OC capability data: {string.Join("; ", capabilities)}");

        var core = GetMaxDeltaMhzFromCapabilities(capabilities, NvidiaGraphicsClockId, DefaultMaxCoreDeltaMhz, 1000);
        var memory = GetMaxDeltaMhzFromCapabilities(capabilities, NvidiaMemoryClockId, DefaultMaxMemoryDeltaMhz, 5000);

        return new(core, memory);
    }

    private static async Task<bool> IsYogaPro14s83BUGpuOcFallbackSupportedAsync()
    {
        try
        {
            if (!await IsYogaPro14s83BUAsync().ConfigureAwait(false))
                return false;

            var lenovoGpuOcClassExists = await WMI.LenovoGpuOverclockingData.ExistsClassAsync().ConfigureAwait(false);
            var gameZoneGpuOcClassExists = await WMI.LenovoGameZoneGpuOCData.ExistsClassAsync().ConfigureAwait(false);
            var isSupported = lenovoGpuOcClassExists || gameZoneGpuOcClassExists;
            Log.Instance.Trace($"YogaPro 14s 83BU GPU OC fallback status: {isSupported}. [LENOVO_GPU_OVERCLOCKING_DATA={lenovoGpuOcClassExists}, LENOVO_GAMEZONE_GPU_OC_DATA={gameZoneGpuOcClassExists}]");
            return isSupported;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"YogaPro 14s 83BU GPU OC fallback check failed.", ex);
            return false;
        }
    }

    private static async Task<bool> IsYogaPro14s83BUAsync()
    {
        var machineInformation = await Compatibility.GetMachineInformationAsync().ConfigureAwait(false);
        return machineInformation.MachineType.Equals(YogaPro14sMachineType, StringComparison.InvariantCultureIgnoreCase);
    }

    private static async Task<GPUOverclockCapabilityData[]> ReadGpuOverclockCapabilitiesAsync()
    {
        var capabilities = await TryReadGpuOverclockCapabilitiesAsync("LENOVO_GPU_OVERCLOCKING_DATA", WMI.LenovoGpuOverclockingData.ReadAsync).ConfigureAwait(false);
        if (capabilities.Length > 0)
            return capabilities;

        return await TryReadGpuOverclockCapabilitiesAsync("LENOVO_GAMEZONE_GPU_OC_DATA", WMI.LenovoGameZoneGpuOCData.ReadAsync).ConfigureAwait(false);
    }

    private static async Task<GPUOverclockCapabilityData[]> TryReadGpuOverclockCapabilitiesAsync(string source, Func<Task<IEnumerable<GPUOverclockCapabilityData>>> readAsync)
    {
        try
        {
            var capabilities = (await readAsync().ConfigureAwait(false)).ToArray();
            Log.Instance.Trace($"GPU OC capability source '{source}' returned {capabilities.Length} entries.");
            return capabilities;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"GPU OC capability source '{source}' unavailable.", ex);
            return [];
        }
    }

    private static void SetOverclockInfo(PhysicalGPU gpu, GPUOverclockInfo info, GPUOverclockInfo maxDeltaMhz)
    {
        var coreDelta = Math.Clamp(info.CoreDeltaMhz, 0, maxDeltaMhz.CoreDeltaMhz);
        var memoryDelta = Math.Clamp(info.MemoryDeltaMhz, 0, maxDeltaMhz.MemoryDeltaMhz);

        var clockEntries = new[]
        {
            new PerformanceStates20ClockEntryV1(PublicClockDomain.Graphics, new PerformanceStates20ParameterDelta(coreDelta * 1000)),
            new PerformanceStates20ClockEntryV1(PublicClockDomain.Memory, new PerformanceStates20ParameterDelta(memoryDelta * 1000))
        };
        var voltageEntries = Array.Empty<PerformanceStates20BaseVoltageEntryV1>();
        var performanceStateInfo = new[] { new PerformanceStates20InfoV1.PerformanceState20(PerformanceStateId.P0_3DPerformance, clockEntries, voltageEntries) };

        var overclock = new PerformanceStates20InfoV1(performanceStateInfo, 2, 0);
        GPUApi.SetPerformanceStates20(gpu.Handle, overclock);
    }

    private static int GetMaxDeltaMhzFromCapabilities(GPUOverclockCapabilityData[] capabilities, int clockId, int fallback, int hardCap)
    {
        return capabilities
            .Where(c => c.ClockId == clockId)
            .Select(c => NormalizeDeltaMhz(c.MaxOffset, c.OffsetScale))
            .Where(v => v > 0 && v <= hardCap)
            .DefaultIfEmpty(fallback)
            .Max();
    }

    private static int NormalizeDeltaMhz(int value, int scale)
    {
        if (value <= 0)
            return 0;

        if (scale > 1 && value % scale == 0)
        {
            var scaled = value / scale;
            if (scaled > 0)
                return scaled;
        }

        if (value >= 10000 && value % 1000 == 0)
            return value / 1000;

        return value;
    }

    private static GPUOverclockInfo GetOverclockInfo(PhysicalGPU gpu)
    {
        var states = GPUApi.GetPerformanceStates20(gpu.Handle);
        var core = states.Clocks[PerformanceStateId.P0_3DPerformance][0].FrequencyDeltaInkHz.DeltaValue / 1000;
        var memory = states.Clocks[PerformanceStateId.P0_3DPerformance][1].FrequencyDeltaInkHz.DeltaValue / 1000;
        return new(core, memory);
    }
}
