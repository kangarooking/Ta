using System.Diagnostics;
using Ta.Windows.Platform.Interop;

namespace Ta.Windows.Platform.Diagnostics;

public static class ProcessMemoryService
{
    public static void TrimIdleWorkingSet()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
        using var process = Process.GetCurrentProcess();
        NativeMethods.SetProcessWorkingSetSize(process.Handle, new nint(-1), new nint(-1));
    }
}
