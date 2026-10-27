using System.Diagnostics;
using System.Text;

namespace AdbSolver.Core;

public sealed record DeviceInfo
{
    public string Serial { get; init; } = "";
    public string State { get; init; } = "";       // device / offline / unauthorized / unknown
    public string Model { get; init; } = "";
    public string Brand { get; init; } = "";
    public string AndroidVersion { get; init; } = "";
    public string Sdk { get; init; } = "";
    public string Resolution { get; init; } = "";
    public int BatteryLevel { get; init; } = -1;
    public string Connection { get; init; } = "";   // USB / WiFi
    public bool IsReady => State == "device";

    public string Summary
    {
        get
        {
            if (!IsReady) return $"{Serial}  [{State}]";
            var b = BatteryLevel >= 0 ? $" · {BatteryLevel}%" : "";
            return $"{Brand} {Model} · Android {AndroidVersion} (API {Sdk}) · {Resolution} · {Connection}{b}";
        }
    }

    public string StateLabel => State switch
    {
        "device" => "在线",
        "offline" => "离线",
        "unauthorized" => "未授权（手机上点允许）",
        "bootloader" => "Bootloader",
        "recovery" => "Recovery",
        "no permissions" => "无权限",
        _ => State,
    };
}
