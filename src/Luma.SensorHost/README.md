# Optional CPU / DIMM temperature host

This executable is separate from the ordinary-user overlay. It uses the pinned,
unmodified LibreHardwareMonitorLib 0.9.6 NuGet package. Nothing opens a hardware
provider at process startup or while the service waits without sensor requests.

## Integration

- Service name: `LumaIslandSensors`; local pipe: `LumaIsland.SensorHost.v1`.
- Overlay API: `SensorClient.Read(bool cpu, bool memory) -> SensorResponse`.
  Constructing the client does no IPC. Call `Read` from the existing sampling
  worker, at most once every two seconds. Dispose it when both metrics are off.
- Response fields: `Version`, `SampledAtUtc`, nullable `CpuCelsius` and
  `MemoryCelsius`, plus `CpuStatus` and `MemoryStatus`. The overlay translates
  status codes rather than showing service exception text.
- Status codes: `ok`, `disabled`, `unavailable`, `driver_missing`,
  `provider_error`; the client can additionally return `not_installed`,
  `access_denied`, `timeout`, and `protocol_error`.
- Wire: one camelCase UTF-8 JSON object and LF, at most 4096 payload bytes,
  version 1, exactly one outstanding request, unknown fields rejected. Request:
  `{"version":1,"cpu":true,"memory":false}`. No executable paths, hardware
  addresses, driver commands, fan controls, or tuning parameters are accepted.
- A cold read has a three-second deadline; a connected read has 750 ms.
  Replies older than eight seconds or over two seconds in the future, malformed
  replies, and invalid temperatures yield null values. Bounds are -40..150 C
  for CPU and -40..125 C for DIMMs; these are plausibility bounds, not limits
  recommended for the hardware.

## Explicit setup

Publish this project as a separate self-contained Windows x64 folder and ship
that folder as `sensors` beside the overlay. On an explicit setup click, launch
its executable elevated with `--install --owner-sid <original-user-account-SID>`.
Removal is `--remove`, also elevated. `--version` and `--help` are non-installing.
There is no automatic elevation, downloading, or driver installation.

The installer copies the complete published folder under
`%ProgramFiles%/LumaIsland/Sensors/0.8.0`, sets administrators as owner and grants
SYSTEM/administrators full access and ordinary users read/execute. It registers
a LocalSystem automatic-start service. Idle startup waits on the pipe and does
not initialize LHM. Failed upgrades attempt to restore the preceding binary and
service command; rollback failures are reported instead of silently ignored.
Setup records a bounded, atomically replaced log at
`%ProgramData%/LumaIsland/Sensors/setup.log` when logging is possible.

The pipe rejects remote clients, keeps one first-instance listener for its
entire lifetime, and grants only SYSTEM, administrators, and the specified owner
access. The owner's rights exclude creating pipe instances. The client verifies
the pipe server PID against the running SCM service PID. Connection loss,
six seconds without a complete request, or an explicit all-off request closes
the sensor source. Hardware sampling is serialized and limited to one request
per two seconds across reconnects. An already executing driver call cannot be
preempted; it finishes before the source can close.

The user must separately install the official signed
[PawnIO driver](https://pawnio.eu/). Do not bundle an unofficial replacement or
silently weaken Windows driver policy. Hardware/firmware access can still be
unavailable on particular machines, or while another monitor holds a bus lock.

## What is measured

CPU selects Intel `CPU Package`, or AMD `Core (Tctl/Tdie)`, then `Core (Tdie)`,
then `Core (Tctl)` per processor; the maximum across processors is returned.
RAM selects only live DIMM temperature index 0 and returns the maximum valid
reading. Static thermal thresholds and sensor resolution are excluded. A DIMM
must expose a thermal sensor through a supported controller; DDR4/DDR5 model
information alone cannot guarantee a reading.

LHM groups other than CPU and memory are disabled. Non-DIMM memory load devices
are not updated. Library history retention is disabled. Because LHM 0.9.6 keeps
the old DIMM value after a failed update, selected cached sensor values are
cleared before each update through the existing managed property setter; no
hardware register is written by that cache reset. RAMSPDToolkit's global driver
handles are explicitly released after `Computer.Close`.

Primary source references: [DIMM selection](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Memory/DimmMemory.cs),
[failed update semantics](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Memory/Sensors/SpdThermalSensor.cs),
[library dependency versions](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/LibreHardwareMonitorLib.csproj).
The library and several transitive packages are MPL-2.0; they are not relicensed
under Luma's MIT license. Ship their required licenses, source links, and other
dependency notices with the separate host payload.

`tests/SensorHostChecks` exercises mapping, lifecycle, bounded framing, response
validation, path/SID validation, and a real private named pipe with its actual
DACL, peer PID, first-instance exclusion and EOF/reconnect. It never installs a
service/driver or initializes a physical hardware source. Elevated install,
upgrade, removal and real hardware readings therefore require a separate
explicit acceptance check on the target machine; the tests do not prove them.
