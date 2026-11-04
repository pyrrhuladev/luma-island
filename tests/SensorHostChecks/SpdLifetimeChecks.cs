using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;
using Luma.SensorHost;
using RAMSPDToolkit.I2CSMBus;
using RAMSPDToolkit.I2CSMBus.Interop;
using RAMSPDToolkit.I2CSMBus.Interop.PawnIO;
using RAMSPDToolkit.SPD;
using RAMSPDToolkit.Windows.Driver.Interfaces;

internal static class SpdLifetimeChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        Type actualModuleType = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.RAMSPDToolkitDriver+PawnIOModule", true)!;
        // A disposed actual LHM module has precisely this null managed pointer. No PawnIO constructor,
        // module loader, driver handle or physical bus is used anywhere in these checks.
        var actualModule = (IPawnIOModule)Activator.CreateInstance(actualModuleType, new object?[] { null })!;
        ((IDisposable)actualModule).Dispose();
        object oldVictim = NewAccessor(typeof(DDR5Accessor), new TestBus(actualModule));
        try
        {
            typeof(SPDAccessor).GetMethod("Finalize", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(oldVictim, null);
            throw new Exception("Original pinned finalizer crash was not reproduced.");
        }
        catch (TargetInvocationException exception)
        {
            check(exception.InnerException is NullReferenceException, "Pinned DDR5 SPDAccessor finalizer reproduces the reported NRE with raw disposed LHM module");
        }

        var inner = new TestDriver(actualModule);
        var guarded = new GuardedSpdDriver(inner);
        IPawnIOModule proxy = guarded.LoadModule(PawnIOSMBusIdentifier.I801);
        guarded.Unload(); guarded.Unload();
        check(inner.Unloads == 1 && !guarded.IsOpen && !guarded.Load(), "Driver closes exactly once and cannot reopen a released lifetime");
        long[] output = { 77, 88 };
        check(proxy.Execute("late-finalizer", Array.Empty<long>(), 0, output, 2, out uint returned) < 0 && returned == 0 && output[0] == 0 && output[1] == 0,
            "Closed proxy returns explicit failure with cleared data instead of reaching the disposed actual module");
        check(guarded.LoadModule(PawnIOSMBusIdentifier.I801) is null, "No module can be loaded after closure");
        var bus = new TestBus(proxy);
        var weak = new WeakReference[24];
        for (int index = 0; index < weak.Length; index++) weak[index] = QueueFinalizer(index % 2 == 0 ? typeof(DDR5Accessor) : typeof(DDR4Accessor), bus);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        check(Array.TrueForAll(weak, reference => !reference.IsAlive) && bus.Calls >= weak.Length,
            "Actual pinned DDR4/DDR5 finalizers run on the GC thread after driver shutdown without process termination or physical access");

        var blocking = new BlockingModule();
        var concurrentInner = new TestDriver(blocking);
        var concurrent = new GuardedSpdDriver(concurrentInner);
        IPawnIOModule concurrentProxy = concurrent.LoadModule(PawnIOSMBusIdentifier.I801);
        Task<int> read = Task.Run(() => concurrentProxy.Execute("in-flight", Array.Empty<long>(), 0, new long[1], 1, out _));
        check(blocking.Entered.Wait(TimeSpan.FromSeconds(2)), "Synthetic read reaches its deterministic in-flight barrier");
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task close = Task.Run(() => { closeStarted.SetResult(); concurrent.Unload(); });
        await closeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(30);
        check(!close.IsCompleted && concurrentInner.Unloads == 0, "Unload waits for an executing module instead of disposing it underneath the call");
        blocking.Release.Set();
        await Task.WhenAll(read, close).WaitAsync(TimeSpan.FromSeconds(2));
        check(await read == 0 && concurrentInner.Unloads == 1 && !concurrentInner.ClosedWhileExecuting,
            "In-flight read completes before underlying module disposal");
        check(concurrentProxy.Execute("late", Array.Empty<long>(), 0, new long[1], 1, out _) < 0 && blocking.Calls == 1,
            "All later calls fail locally after the synchronized close");
        var factory = GuardedSpdDriver.CreateLhm();
        check(factory.IsOpen, "Pinned LHM driver adapter type can be created without loading a module");
        factory.Unload();
    }

    private static object NewAccessor(Type type, SMBusInterface bus)
    {
        object accessor = RuntimeHelpers.GetUninitializedObject(type);
        GC.SuppressFinalize(accessor);
        typeof(SPDAccessor).GetField("_Bus", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(accessor, bus);
        typeof(SPDAccessor).GetField("_Address", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(accessor, (byte)0x50);
        return accessor;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference QueueFinalizer(Type type, SMBusInterface bus)
    {
        object accessor = NewAccessor(type, bus);
        GC.ReRegisterForFinalize(accessor);
        return new WeakReference(accessor);
    }
    private sealed class TestBus(IPawnIOModule module) : SMBusInterface
    {
        internal int Calls;
        protected override int I2CSMBusXfer(byte address, byte readWrite, byte command, int size, SMBusData data)
        { Interlocked.Increment(ref Calls); return module.Execute("fake-bus", Array.Empty<long>(), 0, new long[1], 1, out _); }
        protected override int I2CXfer(byte address, byte readWrite, int? size, byte[] data) => -19;
    }
    private sealed class TestDriver(IPawnIOModule module) : IPawnIODriver
    {
        internal int Unloads;
        internal bool ClosedWhileExecuting;
        public bool IsOpen => Unloads == 0;
        public bool Load() => IsOpen;
        public IPawnIOModule LoadModule(PawnIOSMBusIdentifier identifier) => module;
        public void Unload()
        {
            if (module is BlockingModule blocking) ClosedWhileExecuting = blocking.Executing;
            Interlocked.Increment(ref Unloads);
            (module as IDisposable)?.Dispose();
        }
    }
    private sealed class BlockingModule : IPawnIOModule
    {
        internal readonly ManualResetEventSlim Entered = new(), Release = new();
        internal volatile bool Executing;
        internal int Calls;
        public int Execute(string name, long[] input, uint inputSize, long[] output, uint outputSize, out uint returned)
        {
            Interlocked.Increment(ref Calls); Executing = true; Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Synthetic read barrier timed out.");
            Executing = false; returned = 1; output[0] = 50; return 0;
        }
    }
}
