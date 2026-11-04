using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Luma.Island.Services;
using Luma.SensorHost;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    private static int _checks;
    private static async Task<int> Main()
    {
        try
        {
            Mapping(); Lifecycle(); Responses(); InstallPaths();
            await Frames();
            await NativePipe();
            Console.WriteLine($"PASS: {_checks} sensor-host checks. Fake sensors and a private test pipe only; no service, driver, or hardware access.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
    private static void Mapping()
    {
        TemperatureReading C(string device, string name, double? value) => new(device, false, device + "/temperature/0", name, value);
        TemperatureReading M(int dimm, int sensor, string name, double? value) => new($"/memory/dimm/{dimm}", true, $"/memory/dimm/{dimm}/temperature/{sensor}", name, value);
        var selected = TemperatureMapping.Select(new[] { C("/intelcpu/0", "CPU Package", 65), C("/intelcpu/0", "CPU Core #1", 99),
            C("/intelcpu/1", "CPU Package", 72), M(0, 0, "DIMM #0", 44), M(1, 0, "DIMM #1", 49),
            M(0, 1, "Temperature Sensor Resolution", .25), M(0, 3, "Thermal Sensor High Limit", 95) });
        Check(selected.Cpu == 72 && selected.Memory == 49, "Max packages and real DIMMs; no core or static limit masquerading as temperature");
        selected = TemperatureMapping.Select(new[] { C("/amdcpu/0", "Core (Tctl)", 82), C("/amdcpu/0", "Core (Tdie)", 62),
            C("/amdcpu/0", "Core (Tctl/Tdie)", 61), C("/amdcpu/0", "CCDs Max (Tdie)", 95) });
        Check(selected.Cpu == 61, "AMD combined package sensor outranks offsets and CCD values");
        Check(TemperatureMapping.Select(new[] { C("/amdcpu/0", "Core (Tctl)", 80), C("/amdcpu/0", "Core (Tdie)", 60) }).Cpu == 60, "AMD physical Tdie preferred to Tctl");
        Check(TemperatureMapping.Select(new[] { C("/amdcpu/0", "Core (Tctl)", 80) }).Cpu == 80, "Tctl is an explicit final fallback");
        foreach (double? bad in new double?[] { null, double.NaN, double.PositiveInfinity, double.NegativeInfinity, -40.1, 151, double.MaxValue })
        {
            Check(TemperatureMapping.Select(new[] { C("/intelcpu/0", "CPU Package", bad) }).Cpu is null, "Invalid CPU reading excluded");
            Check(TemperatureMapping.Select(new[] { M(0, 0, "DIMM #0", bad) }).Memory is null, "Invalid DIMM reading excluded");
        }
        foreach (double value in new[] { -40, 0, 125 }) Check(SensorProtocol.IsValidCelsius(value, true), "Memory validity boundary accepted");
        Check(SensorProtocol.IsValidCelsius(150) && !SensorProtocol.IsValidCelsius(125.1, true), "Distinct CPU/RAM upper boundaries");
        foreach (string bad in new[] { "/memory/temperature/0", "/memory/dimm/0/temperature/1", "/gpu/temperature/0", "/memory/dimm/-1/temperature/0", "/memory/dimm/../temperature/0", "/memory/dimm/1/temperature/0/extra" })
            Check(!TemperatureMapping.IsDimmTemperature(bad), "Malformed/static/non-DIMM identifiers rejected");
        Check(TemperatureMapping.Select(Array.Empty<TemperatureReading>()) == (null, null), "No sensor means unavailable, not zero");
        var cached = new MutableSensor { Value = 68 };
        Check(LhmTemperatureSource.ResetValue(cached) && cached.Value is null, "Cached LHM-style value cleared before a potentially failed update");
        Check(!LhmTemperatureSource.ResetValue(new ReadOnlySensor()), "Unresettable values cannot be treated as fresh");
        // Probe the pinned library's managed property ABI without invoking a constructor,
        // Computer.Open, or any driver: Sensor.Value's zero-window setter only stores a float.
        Type actualType = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly.GetType("LibreHardwareMonitor.Hardware.Sensor", true)!;
        object actualSensor = RuntimeHelpers.GetUninitializedObject(actualType);
        var actualValue = actualType.GetProperty("Value")!;
        actualValue.SetValue(actualSensor, (float?)66);
        Check(LhmTemperatureSource.ResetValue(actualSensor) && actualValue.GetValue(actualSensor) is null, "Pinned LHM 0.9.6 actual internal Sensor supports the safe cache reset ABI");
    }
    private static void Lifecycle()
    {
        int created = 0;
        var sources = new List<FakeSource>();
        using (var session = new SensorSession((cpu, memory) => { created++; var source = new FakeSource(); sources.Add(source); return source; }))
        {
            Check(created == 0, "Cold construction does not initialize hardware");
            Check(session.Read(new(1, false, false)).CpuStatus == "disabled" && created == 0, "Both off avoids provider creation");
            var first = session.Read(new(1, true, false));
            Check(first.CpuCelsius == 52 && first.MemoryCelsius is null && first.MemoryStatus == "disabled", "Only requested metric exposed");
            session.Read(new(1, true, false));
            Check(created == 1 && sources[0].Reads == 2, "Stable flags reuse one source and perform one explicit read each");
            session.Read(new(1, false, true));
            Check(created == 2 && sources[0].Disposals == 1 && sources[1].Reads == 1, "Flag change disposes the previous source before reopening");
            session.Read(new(1, false, false));
            Check(sources[1].Disposals == 1, "Disable stops and releases the source immediately");
            Expect<InvalidDataException>(() => session.Read(new(2, true, true)), "Protocol mismatch rejected before provider access");
        }
        Check(sources.All(s => s.Disposals == 1), "Every opened source disposed once");
        using (var missing = new SensorSession((_, _) => throw new DriverMissingException()))
            Check(missing.Read(new(1, true, true)).CpuStatus == "driver_missing", "Missing driver produces a truthful status");
        var failing = new FakeSource { Fail = true };
        using (var session = new SensorSession((_, _) => failing))
        {
            var response = session.Read(new(1, true, true));
            Check(response.CpuCelsius is null && response.MemoryCelsius is null && response.CpuStatus == "provider_error", "Read failure cannot reuse an old numeric value");
            Check(failing.Disposals == 1, "Failed source released");
        }
        var disposed = new SensorSession((_, _) => throw new Exception("Must not initialize"));
        disposed.Dispose(); disposed.Dispose();
        Expect<ObjectDisposedException>(() => disposed.Read(new(1, true, true)), "Disposed session cannot restart polling");
        using var client = new SensorClient();
        Check(client.Read(false, false).CpuStatus == "disabled", "Disabled ordinary-user client does not connect or query SCM");
        client.Dispose();
        Check(client.Read(true, true).CpuStatus == "disabled", "Disposed client cannot reopen IPC");
    }
    private static void Responses()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var response = new SensorResponse(1, now, 50, 42, "ok", "ok");
        Check(SensorClient.IsValidResponse(response, true, true, now), "Current valid response accepted");
        foreach (var bad in new[] { response with { Version = 2 }, response with { SampledAtUtc = now.AddSeconds(-9) },
            response with { SampledAtUtc = now.AddSeconds(3) }, response with { CpuCelsius = double.NaN },
            response with { MemoryCelsius = 126 }, response with { CpuStatus = "anything" }, response with { CpuCelsius = null },
            response with { CpuStatus = "unavailable" } })
            Check(!SensorClient.IsValidResponse(bad, true, true, now), "Corrupt/stale/status-inconsistent response rejected");
        Check(!SensorClient.IsValidResponse(response, false, true, now), "Unrequested numeric value rejected");
        Check(SensorClient.IsValidResponse(response with { CpuCelsius = null, CpuStatus = "driver_missing" }, true, true, now), "Unavailable metric stays null with an explicit reason");
    }
    private static void InstallPaths()
    {
        string root = Path.Combine(Path.GetTempPath(), "LumaSensors-test-root");
        Check(ServiceInstaller.IsWithin(Path.Combine(root, "0.8.0", "a.dll"), root), "Owned child is within installation root");
        Check(!ServiceInstaller.IsWithin(root + "-evil/a.dll", root), "Sibling prefix is not an owned path");
        Check(!ServiceInstaller.IsWithin(Path.Combine(root, "..", "a.dll"), root), "Parent traversal is not an owned path");
        string sid = "S-1-5-21-111-222-333-1001";
        Check(SensorPipeServer.ValidateOwnerSid(sid) == sid, "Account SID accepted");
        foreach (string invalid in new[] { "S-1-1-0", "S-1-5-18", "S-1-5-32-544", "S-1-5-11", "bad", sid + " --anything" })
            Expect<ArgumentException>(() => SensorPipeServer.ValidateOwnerSid(invalid), "Broad/system/injected SID rejected");
        string command = ServiceInstaller.BuildCommand(Path.Combine(root, "folder with spaces"), sid);
        Check(command.StartsWith('"') && command.Contains("\" --service --owner-sid " + sid, StringComparison.Ordinal), "SCM executable path quoted; arguments restricted to a validated SID");
    }
    private static async Task Frames()
    {
        using var stream = new MemoryStream();
        await SensorProtocol.WriteAsync(stream, new SensorRequest(1, true, false), CancellationToken.None);
        Check(stream.Length < 128 && stream.ToArray()[^1] == 10, "Wire frame is bounded UTF8 JSON plus LF");
        stream.Position = 0;
        Check(await SensorProtocol.ReadAsync<SensorRequest>(stream, CancellationToken.None) == new SensorRequest(1, true, false), "Request round-trips");
        foreach (string invalid in new[] { "\n", "null\n", "{\"version\":1,\"cpu\":true,\"path\":\"x\"}\n", new string('x', 4097) + "\n", "{}\n{}\n" })
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(invalid));
            await ExpectAsync<Exception>(() => SensorProtocol.ReadAsync<SensorRequest>(input, CancellationToken.None), "Malformed/unknown/oversized/pipelined frames rejected");
        }
        using var truncated = new MemoryStream(Encoding.UTF8.GetBytes("{\"version\":1}"));
        await ExpectAsync<EndOfStreamException>(() => SensorProtocol.ReadAsync<SensorRequest>(truncated, CancellationToken.None), "Truncated peer does not hang or yield a response");
        using var output = new MemoryStream();
        await ExpectAsync<InvalidDataException>(() => SensorProtocol.WriteAsync(output, new string('a', 4096), CancellationToken.None), "Oversized outbound frame rejected");
    }
    private static async Task NativePipe()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string sid = identity.User!.Value;
        string name = "LumaIsland.SensorHost.Checks." + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream server = SensorPipeServer.CreatePipe(sid, name);
        Check(GetSecurityInfo(server.SafePipeHandle, 6, 4, out _, out _, out _, out _, out IntPtr descriptor) == 0, "Actual pipe DACL is readable");
        RawSecurityDescriptor security;
        try
        {
            byte[] binary = new byte[GetSecurityDescriptorLength(descriptor)];
            Marshal.Copy(descriptor, binary, 0, binary.Length);
            security = new RawSecurityDescriptor(binary, 0);
        }
        finally { LocalFree(descriptor); }
        Check(security.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected), "Pipe DACL is protected from inheritance");
        Check(security.DiscretionaryAcl!.Count == 3, "Only SYSTEM, administrators, and explicit owner receive pipe access");
        var ownerAce = security.DiscretionaryAcl.Cast<CommonAce>().Single(a => a.SecurityIdentifier.Value == sid);
        Check((ownerAce.AccessMask & 4) == 0 && (ownerAce.AccessMask & 3) == 3, "Owner has read/write data and cannot create pipe instances");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Task connected = server.WaitForConnectionAsync(timeout.Token);
        SafePipeHandle handle = CreateFileW(@"\\.\pipe\" + name, 0x12019B, 0, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        Check(!handle.IsInvalid, "Real protected native pipe accepts the owner's specific read/write rights");
        using var client = new NamedPipeClientStream(PipeDirection.InOut, true, true, handle);
        await connected;
        Check(GetNamedPipeServerProcessId(handle, out uint serverPid) && serverPid == Environment.ProcessId, "Native peer PID authentication returns the actual server");
        await SensorProtocol.WriteAsync(client, new SensorRequest(1, true, true), timeout.Token);
        Check(await SensorProtocol.ReadAsync<SensorRequest>(server, timeout.Token) == new SensorRequest(1, true, true), "Actual asynchronous native pipe carries the bounded protocol");
        Expect<System.ComponentModel.Win32Exception>(() => { using var duplicate = SensorPipeServer.CreatePipe(sid, name); }, "FIRST_PIPE_INSTANCE rejects a second server for the same name");
        client.Dispose();
        Check(await server.ReadAsync(new byte[1], timeout.Token) == 0, "Accepted pipe observes client EOF");
        server.Disconnect();
        Check(!server.IsConnected, "Private test pipe disconnects cleanly without destroying the protected listener");
        Task reconnected = server.WaitForConnectionAsync(timeout.Token);
        SafePipeHandle secondHandle = CreateFileW(@"\\.\pipe\" + name, 0x12019B, 0, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        Check(!secondHandle.IsInvalid, "Persistent listener accepts a new owner after EOF and explicit Disconnect");
        using var second = new NamedPipeClientStream(PipeDirection.InOut, true, true, secondHandle);
        await reconnected;
        await SensorProtocol.WriteAsync(second, new SensorRequest(1, false, true), timeout.Token);
        Check(await SensorProtocol.ReadAsync<SensorRequest>(server, timeout.Token) == new SensorRequest(1, false, true), "Re-enabled client receives a clean request stream");
        second.Dispose(); server.Disconnect();
    }
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); _checks++; }
    private static void Expect<T>(Action action, string label) where T : Exception
    { try { action(); } catch (T) { _checks++; return; } throw new Exception(label); }
    private static async Task ExpectAsync<T>(Func<Task> action, string label) where T : Exception
    { try { await action(); } catch (T) { _checks++; return; } throw new Exception(label); }
    public sealed class MutableSensor { public float? Value { get; set; } }
    private sealed class ReadOnlySensor { public float? Value => 70; }
    private sealed class FakeSource : ITemperatureSource
    {
        internal int Reads, Disposals;
        internal bool Fail;
        public IReadOnlyList<TemperatureReading> Read()
        {
            Reads++; if (Fail) throw new IOException("Synthetic read failure");
            return new[] { new TemperatureReading("/intelcpu/0", false, "/intelcpu/0/temperature/0", "CPU Package", 52),
                new TemperatureReading("/memory/dimm/0", true, "/memory/dimm/0/temperature/0", "DIMM #0", 43) };
        }
        public void Dispose() => Disposals++;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateFileW(string path, uint access, uint share, IntPtr attributes, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern uint GetSecurityInfo(SafePipeHandle handle, int type, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
