using System;

namespace Luma.SensorHost;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "--install" && args[1] == "--owner-sid")
            { ServiceInstaller.Install(args[2]); ServiceInstaller.TryWriteSetupLog("Install succeeded."); }
            else if (args.Length == 1 && args[0] == "--remove")
            { ServiceInstaller.Remove(); ServiceInstaller.TryWriteSetupLog("Remove succeeded. Independently installed drivers were left unchanged."); }
            else if (args.Length == 3 && args[0] == "--service" && args[1] == "--owner-sid") return NativeService.Run(args[2]);
            else if (args.Length == 1 && args[0] == "--version") Console.WriteLine("Luma Island SensorHost " + typeof(Program).Assembly.GetName().Version!.ToString(3) + "; LHM 0.9.6; protocol 1");
            else
            {
                Console.WriteLine("Optional sensor service. Explicit administrator setup: --install --owner-sid <Windows-user-SID>; remove: --remove. PawnIO must be installed separately from its official installer.");
                return args.Length == 0 || args.Length == 1 && args[0] == "--help" ? 0 : 2;
            }
            return 0;
        }
        catch (Exception exception)
        {
            if (args.Length > 0 && args[0] is "--install" or "--remove")
                ServiceInstaller.TryWriteSetupLog(args[0] + " failed: " + exception.Message);
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
