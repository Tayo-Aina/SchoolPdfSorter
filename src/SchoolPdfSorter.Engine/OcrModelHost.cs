using System.Diagnostics;
using NanoOcr;
using NanoOcr.Pipeline;

namespace SchoolPdfSorter.Engine;

/// <summary>
/// Thread-safe lazy holder for the offline OCR engine. A <see cref="NanoOCR"/> instance is
/// documented as thread-safe and is expensive to construct (protobuf parse, constant folding and
/// graph fusion), so one instance per distinct option set is shared by the course-structure import
/// (step 2) and the sorter (step 4), and reused on every later run in the same app session.
///
/// Instances are keyed by the options that change construction. In practice there are two: the
/// 300 DPI structure reader and the 200 DPI sorter. Keeping them alive avoids paying the init
/// cost twice per run; no new models or packages are loaded.
/// </summary>
internal static class OcrModelHost
{
    private static readonly object Gate = new();
    private static readonly Dictionary<Key, NanoOCR> Instances = new();

    private readonly record struct Key(double Dpi, int Threads, ConvertMode Mode);

    /// <summary>Gets a shared engine, reporting how long its first construction took.</summary>
    public static NanoOCR Get(double dpi, int threads, ConvertMode mode, out double initSeconds)
    {
        var key = new Key(dpi, threads, mode);
        lock (Gate)
        {
            if (Instances.TryGetValue(key, out var existing))
            {
                initSeconds = 0;
                return existing;
            }

            var started = Stopwatch.GetTimestamp();
            var ocr = new NanoOCR(new NanoOcrOptions
            {
                Dpi = dpi,
                Threads = threads > 0 ? threads : 1,
                Mode = mode,
                // Keep the dormant stacks unloaded and the same fused graphs as before.
                ClassifyPages = false,
                Math = false,
                KeepImages = false,
                Fusion = true,
            });
            initSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            Instances[key] = ocr;
            return ocr;
        }
    }

    /// <summary>How many distinct option sets have been built (for diagnostics/tests).</summary>
    public static int LoadedInstances
    {
        get { lock (Gate) return Instances.Count; }
    }
}
