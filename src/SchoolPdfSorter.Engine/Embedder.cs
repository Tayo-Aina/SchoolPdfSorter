using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SchoolPdfSorter.Engine;

/// <summary>
/// EmbeddingGemma 2 (q4 ONNX) wrapper: text embeddings and full page-image embeddings
/// (vision encoder -> 280-ish soft tokens -> text encoder). CPU-only, offline.
/// </summary>
public sealed class Embedder : IDisposable
{
    public const string BoiToken = "<|image>";
    public const string ImageToken = "<|image|>";
    public const string EoiToken = "<image|>";
    public const string ClassificationPrefix = "task: classification | query: ";
    public const string QueryPrefix = "task: search result | query: ";

    private readonly Tokenizer _tokenizer;
    private readonly InferenceSession _textSession;
    private readonly InferenceSession _visionSession;
    private readonly float[] _empty = Array.Empty<float>();
    private readonly object _timingLock = new();

    public Tokenizer Tokenizer => _tokenizer;

    public Embedder(string modelsDir, int intraOpThreads = 0)
    {
        _tokenizer = new Tokenizer(Path.Combine(modelsDir, "tokenizer.json"));
        _textSession = new InferenceSession(Path.Combine(modelsDir, "model_q4.onnx"), MakeOptions(intraOpThreads));
        _visionSession = new InferenceSession(Path.Combine(modelsDir, "vision_encoder_q4.onnx"), MakeOptions(intraOpThreads));
    }

    private static SessionOptions MakeOptions(int intraOpThreads)
    {
        var options = new SessionOptions();
        if (intraOpThreads > 0)
            options.IntraOpNumThreads = intraOpThreads;
        return options;
    }

    /// <summary>Embed a short text (course label, query, doc snippet).</summary>
    public float[] EmbedText(string text)
    {
        var ids = _tokenizer.Encode(text).ToArray();
        return RunText(ids, null);
    }

    /// <summary>Embed a course label with the classification prefix (the app's label side).</summary>
    public float[] EmbedCourseLabel(string course)
        => EmbedText(ClassificationPrefix + course);

    public TextTiming? LastTextTimingMs { get; private set; }
    public VisionTiming? LastVisionTimingMs { get; private set; }

    public readonly record struct TextTiming(double PreprocessMs, double RunMs, int Tokens);
    public readonly record struct VisionTiming(double PrepMs, double RunMs, int SoftTokens);

    /// <summary>Embed a rendered page (interleaved RGB bytes) via the vision -> text chain.</summary>
    public float[] EmbedPage(byte[] rgb, int width, int height, out int softTokens, int maxSoftTokens = ImagePrep.DefaultSoftTokens)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var page = ImagePrep.Prepare(rgb, width, height, maxSoftTokens);
        var prepMs = sw.Elapsed.TotalMilliseconds;

        softTokens = page.SoftTokens;
        var pvT = new DenseTensor<float>(page.PixelValues, new[] { 1, page.Patches, ImagePrep.PatchDims });
        var posT = new DenseTensor<long>(page.PositionIds, new[] { 1, page.Patches, 2 });
        var visFeeds = new[]
        {
            NamedOnnxValue.CreateFromTensor("pixel_values", pvT),
            NamedOnnxValue.CreateFromTensor("pixel_position_ids", posT),
        };
        sw.Restart();
        float[] feats;
        using (var vres = _visionSession.Run(visFeeds))
        {
            feats = vres.First(r => r.Name == "image_features").AsTensor<float>().ToArray();
        }
        var visMs = sw.Elapsed.TotalMilliseconds;
        lock (_timingLock)
            LastVisionTimingMs = new VisionTiming(prepMs, visMs, softTokens);

        var template = BoiToken + string.Concat(Enumerable.Repeat(ImageToken, softTokens)) + EoiToken;
        var ids = _tokenizer.Encode(template).ToArray();
        return RunText(ids, feats);
    }

    private float[] RunText(int[] ids, float[]? imageFeats)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var idsLong = new long[ids.Length];
        for (int i = 0; i < ids.Length; i++) idsLong[i] = ids[i];
        var mask = new long[ids.Length];
        Array.Fill(mask, 1L);
        int n = imageFeats is null ? 0 : imageFeats.Length / 512;
        var feeds = new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(idsLong, new[] { 1, ids.Length })),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, new[] { 1, ids.Length })),
            NamedOnnxValue.CreateFromTensor("image_features", new DenseTensor<float>(imageFeats ?? _empty, new[] { n, 512 })),
            NamedOnnxValue.CreateFromTensor("video_features", new DenseTensor<float>(_empty, new[] { 0, 512 })),
            NamedOnnxValue.CreateFromTensor("audio_features", new DenseTensor<float>(_empty, new[] { 0, 512 })),
        };
        using var results = _textSession.Run(feeds);
        foreach (var r in results)
        {
            if (r.Name == "sentence_embedding")
            {
                lock (_timingLock)
                    LastTextTimingMs = new TextTiming(0, sw.Elapsed.TotalMilliseconds, ids.Length);
                return r.AsTensor<float>().ToArray();
            }
        }
        throw new InvalidOperationException("sentence_embedding output missing");
    }

    public static double Cosine(float[] a, float[] b)
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

    public void Dispose()
    {
        _textSession.Dispose();
        _visionSession.Dispose();
    }
}
