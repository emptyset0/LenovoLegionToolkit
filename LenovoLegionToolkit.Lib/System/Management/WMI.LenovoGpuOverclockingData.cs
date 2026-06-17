using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading.Tasks;

// ReSharper disable InconsistentNaming
// ReSharper disable StringLiteralTypo

namespace LenovoLegionToolkit.Lib.System.Management;

public static partial class WMI
{
    public static class LenovoGpuOverclockingData
    {
        public static Task<IEnumerable<GPUOverclockCapabilityData>> ReadAsync() => WMI.ReadAsync("root\\WMI",
            $"SELECT * FROM LENOVO_GPU_OVERCLOCKING_DATA",
            pdc => new GPUOverclockCapabilityData(
                GetInt32(pdc, "ClockID"),
                GetInt32(pdc, "PStateID"),
                GetInt32(pdc, "GpuType"),
                GetInt32(pdc, "mode"),
                GetInt32(pdc, "defaultvalue"),
                GetInt32(pdc, "OCMinOffset"),
                GetInt32(pdc, "OCMaxOffset"),
                GetInt32(pdc, "OCOffsetScale"),
                GetInt32(pdc, "OCOffsetFreq")));

        private static int GetInt32(PropertyDataCollection properties, string propertyName)
        {
            var property = properties
                .Cast<PropertyData>()
                .FirstOrDefault(p => p.Name.Equals(propertyName, StringComparison.InvariantCultureIgnoreCase));

            return property?.Value is null ? 0 : Convert.ToInt32(property.Value);
        }
    }
}
