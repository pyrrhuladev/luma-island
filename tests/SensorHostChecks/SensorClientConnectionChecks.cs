using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Luma.Island.Services;
using Luma.SensorHost;

internal static class SensorClientConnectionChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        await BusyReconnect(check);
        await ColdAndWarmDeadlines(check);
    }
    private static async Task BusyReconnect(Action<bool, string> check)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string name = "LumaIsland.SensorHost.Busy." + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using NamedPipeServerStream server = SensorPipeServer.CreatePipe(identity.User!.Value, name);
        Task accepted = server.WaitForConnectionAsync(deadline.Token);
        using NamedPipeClientStream owner = await SensorClient.OpenPipeAsync(name, (uint)Environment.ProcessId, deadline.Token);
        await accepted;
        using (var blockedDeadline = new CancellationTokenSource(60))
        {
            try
            {
                using var unexpected = await SensorClient.OpenPipeAsync(name, (uint)Environment.ProcessId, blockedDeadline.Token);
                throw new Exception("Busy connect unexpectedly succeeded.");
            }
            catch (OperationCanceledException) { check(true, "Busy instance retries obey cancellation instead of immediately reporting unavailable or waiting forever"); }
        }
        Task<NamedPipeClientStream> reconnect = SensorClient.OpenPipeAsync(name, (uint)Environment.ProcessId, deadline.Token);
        await Task.Delay(40, deadline.Token);
        check(!reconnect.IsCompleted, "The next client waits while the old server session is still connected");
        owner.Dispose();
        check(await server.ReadAsync(new byte[1], deadline.Token) == 0, "Server observes preceding client EOF before simulated cleanup");
        await Task.Delay(40, deadline.Token); // Native instance remains busy while prior resources are released.
        server.Disconnect();
        Task acceptedAgain = server.WaitForConnectionAsync(deadline.Token);
        using NamedPipeClientStream next = await reconnect;
        await acceptedAgain;
        await SensorProtocol.WriteAsync(next, new SensorRequest(1, true, true), deadline.Token);
        check(await SensorProtocol.ReadAsync<SensorRequest>(server, deadline.Token) == new SensorRequest(1, true, true),
            "The same connection attempt succeeds after cleanup without a transient unavailable sample");
        next.Dispose(); server.Disconnect();

        Task rejectedAccepted = server.WaitForConnectionAsync(deadline.Token);
        try
        {
            using var wrongPeer = await SensorClient.OpenPipeAsync(name, (uint)Environment.ProcessId + 1, deadline.Token);
            throw new Exception("Wrong peer PID accepted.");
        }
        catch (IOException) { check(true, "Busy retry does not bypass verified server PID authentication"); }
        await rejectedAccepted; server.Disconnect();
        try
        {
            using var absent = await SensorClient.OpenPipeAsync(name + ".missing", (uint)Environment.ProcessId, deadline.Token);
            throw new Exception("Missing pipe accepted.");
        }
        catch (IOException) { check(true, "Non-busy open failures remain immediate failures rather than a generic retry loop"); }
    }
    private static async Task ColdAndWarmDeadlines(Action<bool, string> check)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string name = "LumaIsland.SensorHost.Deadline." + Guid.NewGuid().ToString("N");
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        using NamedPipeServerStream server = SensorPipeServer.CreatePipe(identity.User!.Value, name);
        Task serve = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(overall.Token);
            await SensorProtocol.ReadAsync<SensorRequest>(server, overall.Token);
            await Task.Delay(3100, overall.Token); // Real RAM reinitialization was measured near the old 3 s limit.
            await SensorProtocol.WriteAsync(server, Sample(), overall.Token);
            await SensorProtocol.ReadAsync<SensorRequest>(server, overall.Token);
            await Task.Delay(900, overall.Token); // A warm read must retain the shorter 750 ms deadline.
            try { await SensorProtocol.WriteAsync(server, Sample(), overall.Token); }
            catch (IOException) { /* Warm client deliberately timed out and closed its connection. */ }
        }, overall.Token);
        int connects = 0;
        using var client = new SensorClient(async token =>
        {
            Interlocked.Increment(ref connects);
            await Task.Delay(100, token); // Connection wait consumes the same cold-operation token.
            return await SensorClient.OpenPipeAsync(name, (uint)Environment.ProcessId, token);
        });
        SensorResponse cold = await Task.Run(() => client.Read(true, true), overall.Token);
        check(cold.CpuCelsius == 52 && cold.MemoryCelsius == 43 && cold.CpuStatus == "ok",
            "Cold connection plus a first reply beyond three seconds succeeds inside the new shared five-second budget");
        SensorResponse warm = await Task.Run(() => client.Read(true, true), overall.Token);
        check(warm.CpuCelsius is null && warm.MemoryCelsius is null && warm.CpuStatus == "timeout" && connects == 1,
            "Warm calls reuse the verified connection and retain the 750 ms deadline without stale numeric data");
        await serve;
    }
    private static SensorResponse Sample() => new(1, DateTimeOffset.UtcNow, 52, 43, "ok", "ok");
}
