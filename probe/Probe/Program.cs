using System.Diagnostics;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Probe;

public class RefText
{
    public string name { get; set; } = "";
    public string text { get; set; } = "";
    public int[] ids { get; set; } = Array.Empty<int>();
    public float[] embedding { get; set; } = Array.Empty<float>();
}

public class RefImage
{
    public string name { get; set; } = "";
    public string png { get; set; } = "";
    public int target_h { get; set; }
    public int target_w { get; set; }
    public int grid_h { get; set; }
    public int grid_w { get; set; }
    public int patches { get; set; }
    public int soft_tokens { get; set; }
    public string template { get; set; } = "";
    public int[] ids { get; set; } = Array.Empty<int>();
    public float[] embedding { get; set; } = Array.Empty<float>();
}

public class RefRoot
{
    public List<RefText> text_cases { get; set; } = new();
    public List<RefImage> image_cases { get; set; } = new();
}

public static class Program
{
    private static int _failures;

    private static void Check(bool ok, string label, string detail = "")
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
        if (!ok) _failures++;
    }

    private static string FindDir(string name)
    {
        var d = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var c = Path.Combine(d, name);
            if (Directory.Exists(c)) return c;
            var p = Directory.GetParent(d);
            if (p is null) break;
            d = p.FullName;
        }
        throw new DirectoryNotFoundException($"could not find '{name}' above {AppContext.BaseDirectory}");
    }

    private static float[] LoadF32(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var arr = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, arr, 0, bytes.Length);
        return arr;
    }

    private static long[] LoadI64(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var arr = new long[bytes.Length / 8];
        Buffer.BlockCopy(bytes, 0, arr, 0, bytes.Length);
        return arr;
    }

    private static double Cos(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb) + 1e-30);
    }

    private static (byte[] rgb, int w, int h) LoadPngRgb(string path)
    {
        using var bmp = SKBitmap.Decode(path) ?? throw new FileNotFoundException("cannot decode " + path);
        int w = bmp.Width, h = bmp.Height;
        var rgb = new byte[w * h * 3];
        var px = bmp.Pixels;
        int j = 0;
        for (int i = 0; i < px.Length; i++)
        {
            rgb[j++] = px[i].Red;
            rgb[j++] = px[i].Green;
            rgb[j++] = px[i].Blue;
        }
        return (rgb, w, h);
    }

    private static bool IdsEqual(List<int> mine, int[] reference, out string detail)
    {
        if (mine.Count != reference.Length)
        {
            detail = $"len {mine.Count} vs {reference.Length}";
            return false;
        }
        for (int i = 0; i < mine.Count; i++)
        {
            if (mine[i] != reference[i])
            {
                int lo = Math.Max(0, i - 2);
                detail = $"diff@{i} mine[{string.Join(",", mine.Skip(lo).Take(5))}] ref[{string.Join(",", reference.Skip(lo).Take(5))}]";
                return false;
            }
        }
        detail = $"{mine.Count} ids";
        return true;
    }

    public static int Main()
    {
        var refsDir = FindDir("refs");
        var probeDir = Directory.GetParent(refsDir)!.FullName;
        var modelsDir = Path.Combine(probeDir, "models");
        var refs = JsonSerializer.Deserialize<RefRoot>(File.ReadAllText(Path.Combine(refsDir, "refs.json")))
                   ?? throw new InvalidDataException("refs.json");

        Console.WriteLine("== C# probe: tokenizer / preprocessing / onnx q4 parity ==");
        var sw = Stopwatch.StartNew();
        var tok = new Tokenizer(Path.Combine(modelsDir, "tokenizer.json"));
        Console.WriteLine($"tokenizer loaded: vocab {tok.VocabSize}, {sw.ElapsedMilliseconds} ms");

        Console.WriteLine("== tokenizer ==");
        foreach (var t in refs.text_cases)
        {
            bool ok = IdsEqual(tok.Encode(t.text), t.ids, out var detail);
            Check(ok, t.name, detail);
        }

        Console.WriteLine("== sessions ==");
        sw.Restart();
        using var textSession = new InferenceSession(Path.Combine(modelsDir, "model_q4.onnx"));
        using var visionSession = new InferenceSession(Path.Combine(modelsDir, "vision_encoder_q4.onnx"));
        Console.WriteLine($"q4 sessions loaded: {sw.ElapsedMilliseconds} ms");

        var empty = Array.Empty<float>();

        float[] RunText(int[] ids, float[]? imageFeats = null)
        {
            var idsLong = new long[ids.Length];
            for (int i = 0; i < ids.Length; i++) idsLong[i] = ids[i];
            var mask = new long[ids.Length];
            Array.Fill(mask, 1L);
            int n = imageFeats is null ? 0 : imageFeats.Length / 512;
            var feeds = new[]
            {
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(idsLong, new[] { 1, ids.Length })),
                NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, new[] { 1, ids.Length })),
                NamedOnnxValue.CreateFromTensor("image_features", new DenseTensor<float>(imageFeats ?? empty, new[] { n, 512 })),
                NamedOnnxValue.CreateFromTensor("video_features", new DenseTensor<float>(empty, new[] { 0, 512 })),
                NamedOnnxValue.CreateFromTensor("audio_features", new DenseTensor<float>(empty, new[] { 0, 512 })),
            };
            using var results = textSession.Run(feeds);
            foreach (var r in results)
                if (r.Name == "sentence_embedding")
                    return r.AsTensor<float>().ToArray();
            throw new InvalidOperationException("no sentence_embedding output");
        }

        Console.WriteLine("== text embeddings ==");
        foreach (var t in refs.text_cases)
        {
            var ids = tok.Encode(t.text);
            if (!IdsEqual(ids, t.ids, out _)) { Check(false, $"{t.name} emb", "ids mismatch, skipped"); continue; }
            var emb = RunText(ids.ToArray());
            double cos = Cos(emb, t.embedding);
            Check(cos > 0.999, $"{t.name} emb", $"cos {cos:F5}");
        }

        {
            var ids = tok.Encode(refs.text_cases[0].text).ToArray();
            var swT = new Stopwatch();
            for (int i = 0; i < 5; i++) { swT.Restart(); RunText(ids); }
            Console.WriteLine($"warm text run: {swT.Elapsed.TotalMilliseconds:F0} ms");
        }

        Console.WriteLine("== images ==");
        foreach (var ic in refs.image_cases)
        {
            var (rgb, w, h) = LoadPngRgb(Path.Combine(refsDir, ic.png));
            var prepSw = Stopwatch.StartNew();
            var prep = ImagePrep.Prepare(rgb, w, h);
            prepSw.Stop();
            Check(prep.TargetW == ic.target_w && prep.TargetH == ic.target_h, $"{ic.name} target",
                $"{prep.TargetW}x{prep.TargetH} vs {ic.target_w}x{ic.target_h}");
            Check(prep.SoftTokens == ic.soft_tokens, $"{ic.name} soft tokens", $"{prep.SoftTokens} vs {ic.soft_tokens}");

            var refU8 = File.ReadAllBytes(Path.Combine(refsDir, ic.name + "_pixels_u8.bin"));
            var refScan = new byte[prep.ResizedU8.Length];
            for (int r = 0; r < prep.GridH; r++)
            {
                for (int c = 0; c < prep.GridW; c++)
                {
                    int patchBase = (r * prep.GridW + c) * 768;
                    for (int ph = 0; ph < 16; ph++)
                    {
                        int imgRow = r * 16 + ph;
                        for (int pw = 0; pw < 16; pw++)
                        {
                            int dst = (imgRow * prep.TargetW + c * 16 + pw) * 3;
                            int src = patchBase + (ph * 16 + pw) * 3;
                            refScan[dst] = refU8[src];
                            refScan[dst + 1] = refU8[src + 1];
                            refScan[dst + 2] = refU8[src + 2];
                        }
                    }
                }
            }
            long maxD = 0, sumD = 0, nDiff = 0;
            for (int i = 0; i < refScan.Length; i++)
            {
                long d = Math.Abs(refScan[i] - prep.ResizedU8[i]);
                if (d > 0) { nDiff++; sumD += d; }
                if (d > maxD) maxD = d;
            }
            Check(maxD <= 1, $"{ic.name} resize", $"maxdiff {maxD}, {nDiff}/{refScan.Length} bytes differ, mean {sumD / (double)Math.Max(1, nDiff):F3}");

            var refPos = LoadI64(Path.Combine(refsDir, ic.name + "_position_ids.i64.bin"));
            bool posOk = refPos.Length == prep.PositionIds.Length;
            if (posOk)
                for (int i = 0; i < refPos.Length; i++)
                    if (refPos[i] != prep.PositionIds[i]) { posOk = false; break; }
            Check(posOk, $"{ic.name} positions");

            var refPv = LoadF32(Path.Combine(refsDir, ic.name + "_pixel_values.f32.bin"));
            double pvMax = 0;
            for (int i = 0; i < refPv.Length; i++) pvMax = Math.Max(pvMax, Math.Abs(refPv[i] - prep.PixelValues[i]));
            Check(pvMax <= 4.0 / 255.0, $"{ic.name} pixel floats", $"maxdiff {pvMax:F5}");

            var pvT = new DenseTensor<float>(prep.PixelValues, new[] { 1, ImagePrep.MaxPatches, ImagePrep.PatchDims });
            var posT = new DenseTensor<long>(prep.PositionIds, new[] { 1, ImagePrep.MaxPatches, 2 });
            var visFeeds = new[]
            {
                NamedOnnxValue.CreateFromTensor("pixel_values", pvT),
                NamedOnnxValue.CreateFromTensor("pixel_position_ids", posT),
            };
            var visSw = Stopwatch.StartNew();
            float[] feats;
            using (var vres = visionSession.Run(visFeeds))
            {
                feats = vres.First(r => r.Name == "image_features").AsTensor<float>().ToArray();
            }
            visSw.Stop();
            Check(feats.Length / 512 == ic.soft_tokens, $"{ic.name} feats N", $"{feats.Length / 512} vs {ic.soft_tokens}");
            var refFeats = LoadF32(Path.Combine(refsDir, ic.name + "_image_features.f32.bin"));
            double featCos = Cos(feats, refFeats);
            Check(featCos > 0.999, $"{ic.name} vision feats", $"cos {featCos:F5}");

            var imgIds = tok.Encode(ic.template);
            Check(IdsEqual(imgIds, ic.ids, out var idDetail), $"{ic.name} image ids", idDetail);
            var emb = RunText(imgIds.ToArray(), feats);
            double embCos = Cos(emb, ic.embedding);
            Check(embCos > 0.999, $"{ic.name} image embedding",
                $"cos {embCos:F5}, prep {prepSw.ElapsedMilliseconds} ms, vision {visSw.Elapsed.TotalMilliseconds:F0} ms");
        }

        Console.WriteLine($"peak working set: {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:F0} MB");
        Console.WriteLine(_failures == 0 ? "== ALL PASS ==" : $"== {_failures} FAILURES ==");
        return _failures == 0 ? 0 : 1;
    }
}
