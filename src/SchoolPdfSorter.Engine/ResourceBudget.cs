using System.Runtime.InteropServices;

namespace SchoolPdfSorter.Engine;

/// <summary>
/// One shared concurrency budget for the in-process OCR agents. The ceiling never exceeds
/// <see cref="Environment.ProcessorCount"/>, so a run uses the same or less processing power than
/// the old code. In steady state we use physical-core workers with one kernel thread each
/// (no hyper-thread ALU contention); in the tail all budgeted threads are donated to the few
/// remaining page jobs so a single page still finishes at full-core speed.
/// </summary>
internal static class ResourceBudget
{
    /// <summary>Logical processors visible to Windows (the old hard ceiling).</summary>
    public static int LogicalCores { get; } = Environment.ProcessorCount;

    /// <summary>Physical processor cores, with the documented laptop-safe fallback.</summary>
    public static int PhysicalCores { get; } = GetPhysicalCores();

    /// <summary>
    /// Total compute thread budget B. An explicit thread count is capped at the logical core
    /// count; otherwise a conservative laptop budget leaves SMT/UI headroom.
    /// </summary>
    public static int TotalBudget(int configuredThreads)
    {
        int requested = configuredThreads > 0
            ? Math.Min(configuredThreads, LogicalCores)
            : DefaultLaptopBudget(LogicalCores);

        // A test or the benchmark can pin the whole budget without changing the public options.
        int envThreads = ReadOverride("SCHOOLPDF_THREADS");
        if (envThreads > 0)
            requested = Math.Min(envThreads, LogicalCores);

        return Math.Clamp(requested, 1, LogicalCores);
    }

    /// <summary>
    /// Number of parallel OCR workers W. Bounded by physical cores and by available RAM so W
    /// pooled ONNX tensor arenas cannot push an 8 GB student laptop into paging.
    /// </summary>
    public static int OcrWorkers(int budget, int configuredWorkers = 0)
    {
        int overrideWorkers = ReadOverride("SCHOOLPDF_WORKERS");
        int requested = configuredWorkers > 0 ? configuredWorkers : overrideWorkers;

        int cpuCap;
        if (requested > 0)
        {
            cpuCap = requested;
        }
        else
        {
            // Physical cores, or the plan's fallback of C/2 on 8+ logical cores.
            cpuCap = PhysicalCores > 0 ? PhysicalCores : DefaultLaptopBudget(LogicalCores);
        }

        // Even on a 16 GB laptop, a thin machine has one L3 and one memory controller shared
        // with the WPF UI. Four single-threaded page workers are the safe steady state; an
        // explicit option/env override can still use the full physical-core budget.
        if (requested <= 0)
            cpuCap = Math.Min(cpuCap, 4);

        cpuCap = Math.Clamp(cpuCap, 1, budget);

        // ~192 MB per worker covers the fused model arena plus a few rendered A4 pages.
        // If the OS reports no usable memory figure, keep the CPU-derived cap.
        const long perWorkerBytes = 192L * 1024 * 1024;
        long available = 0;
        try { available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; }
        catch { /* keep CPU cap */ }

        int ramCap = available > 0
            ? Math.Clamp((int)(available / perWorkerBytes), 1, budget)
            : budget;

        return Math.Clamp(Math.Min(cpuCap, ramCap), 1, budget);
    }

    /// <summary>Maximum parallelism for the cheap PdfPig / PptxText extraction stage.</summary>
    public static int SignalWorkers(int budget)
        => Math.Clamp(Math.Min(PhysicalCores > 0 ? PhysicalCores : DefaultLaptopBudget(LogicalCores), budget), 1, 4);

    /// <summary>
    /// Kernel threads inside one page job. Steady state: 1 (W single-threaded workers).
    /// Tail phase with few active jobs: donate the rest of the budget to each job.
    /// </summary>
    public static int ThreadsPerPage(int budget, int workers, int activePages)
    {
        if (activePages <= 0)
            return 1;
        return Math.Clamp(activePages >= workers ? 1 : Math.Max(1, budget / activePages), 1, budget);
    }

    /// <summary>The fallback from the plan: all cores on small CPUs, half on 8+ logical cores.</summary>
    public static int DefaultLaptopBudget(int logicalCores)
        => logicalCores <= 4 ? Math.Max(1, logicalCores) : Math.Max(2, logicalCores / 2);

    private static int ReadOverride(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out int value) && value > 0 ? value : 0;
    }

    /// <summary>
    /// Windows physical-core count via GetLogicalProcessorInformation. The struct layout uses
    /// only the two leading fields (mask, relationship), so it is stable across the union size.
    /// Any failure falls back to the plan's C/2 heuristic.
    /// </summary>
    private static int GetPhysicalCores()
    {
        try
        {
            uint length = 0;
            GetLogicalProcessorInformation(IntPtr.Zero, ref length);
            if (length == 0)
                return Fallback();

            int size = Marshal.SizeOf<ProcessorInfo>();
            IntPtr buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetLogicalProcessorInformation(buffer, ref length))
                    return Fallback();

                int cores = 0;
                for (int offset = 0; offset + size <= length; offset += size)
                {
                    var info = Marshal.PtrToStructure<ProcessorInfo>(IntPtr.Add(buffer, offset));
                    // RelationProcessorCore == 0: one entry per physical core.
                    if (info.Relationship == 0)
                        cores++;
                }
                return cores > 0 && cores <= LogicalCores ? cores : Fallback();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { return Fallback(); }
    }

    private static int Fallback()
        => LogicalCores <= 4 ? Math.Max(1, LogicalCores) : Math.Max(2, LogicalCores / 2);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct ProcessorInfo
    {
        [FieldOffset(0)] public UIntPtr ProcessorMask;
        [FieldOffset(8)] public int Relationship;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint length);
}
