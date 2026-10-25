namespace SchoolPdfSorter.Engine;

public sealed record CourseScore(string Course, double LabelCos, double BestExemplarCos, double Score, bool CodeMatch);

public sealed record PlanItem(
    FileSignals File,
    PlanAction Action,
    string? Course,
    IReadOnlyList<CourseScore> Top,
    string Reason,
    string Tier)
{
    public bool IsMove => Action == PlanAction.Move;
}

public sealed class PlannerOptions
{
    /// <summary>Minimum top-1 score to auto-move (label/exemplar cosine).</summary>
    public double HighTop1 { get; set; } = 0.60;
    /// <summary>Minimum top1-top2 margin to auto-move.</summary>
    public double HighMargin { get; set; } = 0.045;
    /// <summary>A single tier-0 code match moves a file if its score is at least this.</summary>
    public double CodeMatchMinScore { get; set; } = 0.35;
    /// <summary>Run the vision tier when the text-tier margin is below this.</summary>
    public double VisionMarginTrigger { get; set; } = 0.12;
    /// <summary>Vision tier runs when the text top-1 is below this.</summary>
    public double VisionTop1Trigger { get; set; } = 0.50;
    /// <summary>Weight applied to exemplar cosines when mixing with label cosines.</summary>
    public double ExemplarWeight { get; set; } = 0.97;

    /// <summary>
    /// Vision soft-token budget: vision attention is quadratic in patches = soft tokens x 9, so
    /// 140 is ~2.6x faster than 280 and 70 ~5x. Bench-validated on the 38-file corpus: 140 gives
    /// identical accuracy to 280 (33 auto-moves, 0 wrong, 5 queue); 70 loses 2 files to the queue.
    /// </summary>
    public int VisionSoftTokens { get; set; } = 140;

    /// <summary>How many pages to embed through the vision model concurrently.</summary>
    public int VisionWorkers { get; set; } = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 4));
}

/// <summary>Course label + exemplar scoring and the high/queue decision.</summary>
public sealed class Planner
{
    private sealed class CourseState
    {
        public required string Name;
        public required float[] LabelEmb;
        public string? Code;
        public List<float[]> Exemplars = new();
    }

    private readonly List<CourseState> _courses;
    private readonly PlannerOptions _opt;

    public Planner(Embedder embedder, IEnumerable<string> courses, PlannerOptions? options = null)
    {
        _opt = options ?? new PlannerOptions();
        _courses = courses
            .Select(c => new CourseState
            {
                Name = c,
                Code = CourseCodes.ExtractCode(c),
                LabelEmb = embedder.EmbedCourseLabel(c),
            })
            .ToList();
    }

    public PlannerOptions Options => _opt;
    public IReadOnlyList<string> Courses => _courses.Select(c => c.Name).ToList();
    public int ExemplarCount => _courses.Sum(c => c.Exemplars.Count);

    public void AddExemplar(string course, float[] embedding)
    {
        var cs = _courses.FirstOrDefault(c => c.Name == course);
        if (cs is not null) cs.Exemplars.Add(embedding);
    }

    /// <summary>Score one file embedding against every course (labels + exemplars), sorted descending.</summary>
    public CourseScore[] Score(float[] fileEmb, string codeHaystack)
    {
        var results = new List<CourseScore>(_courses.Count);
        foreach (var cs in _courses)
        {
            double label = Embedder.Cosine(fileEmb, cs.LabelEmb);
            double ex = 0;
            foreach (var e in cs.Exemplars)
                ex = Math.Max(ex, Embedder.Cosine(fileEmb, e));
            double score = Math.Max(label, ex * _opt.ExemplarWeight);
            bool codeMatch = cs.Code is not null && CourseCodes.ContainsCode(codeHaystack, cs.Code);
            results.Add(new CourseScore(cs.Name, label, ex, score, codeMatch));
        }
        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        return results.ToArray();
    }

    /// <summary>Combine per-modality scores (text, vision) with per-course max.</summary>
    public static CourseScore[] Combine(CourseScore[]? a, CourseScore[]? b)
    {
        if (a is null || a.Length == 0) return b ?? Array.Empty<CourseScore>();
        if (b is null || b.Length == 0) return a;
        var map = a.ToDictionary(s => s.Course);
        var merged = new List<CourseScore>(a.Length);
        foreach (var sb in b)
        {
            if (!map.TryGetValue(sb.Course, out var sa)) { merged.Add(sb); continue; }
            var best = sa.Score >= sb.Score ? sa : sb;
            merged.Add(best with { CodeMatch = sa.CodeMatch || sb.CodeMatch });
        }
        foreach (var sa in a) if (!b.Any(x => x.Course == sa.Course)) merged.Add(sa);
        merged.Sort((x, y) => y.Score.CompareTo(x.Score));
        return merged.ToArray();
    }

    public bool TryDecide(CourseScore[] scores, out string course, out string reason)
    {
        course = "";
        reason = "";
        if (scores.Length == 0) { reason = "no courses"; return false; }

        var codeMatches = scores.Where(s => s.CodeMatch).ToList();
        if (codeMatches.Count == 1 && codeMatches[0].Score >= _opt.CodeMatchMinScore)
        {
            course = codeMatches[0].Course;
            reason = $"code match ({course})";
            return true;
        }
        if (codeMatches.Count > 1)
        {
            // multiple codes present: ambiguous by code; fall through to embedding rule but only among them
            var top = codeMatches[0];
            double second = codeMatches.Count > 1 ? codeMatches[1].Score : 0;
            if (top.Score >= _opt.HighTop1 && top.Score - second >= _opt.HighMargin)
            {
                course = top.Course;
                reason = $"multi-code, strongest ({course})";
                return true;
            }
            reason = "multiple course codes";
            return false;
        }

        double top1 = scores[0].Score;
        double top2 = scores.Length > 1 ? scores[1].Score : 0;
        if (top1 >= _opt.HighTop1 && top1 - top2 >= _opt.HighMargin)
        {
            course = scores[0].Course;
            reason = $"strong match {top1:F3} vs {top2:F3}";
            return true;
        }
        reason = $"weak: {top1:F3} vs {top2:F3}";
        return false;
    }
}
