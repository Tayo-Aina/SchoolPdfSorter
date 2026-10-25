using System.Text;
using System.Text.Json;

namespace Probe;

/// <summary>
/// Minimal C# port of the HF Gemma tokenizer: added-token splitting, space->U+2581 normalizer,
/// rank-priority BPE with byte fallback, BOS/EOS wrapping. Validate against refs.json token ids.
/// </summary>
public sealed class Tokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<string, int> _merges = new(StringComparer.Ordinal);
    private readonly List<(string Content, int Id)> _added;
    private readonly Dictionary<string, int> _byteTokens = new(StringComparer.Ordinal);
    private readonly int _bos, _eos, _unk;

    public int VocabSize => _vocab.Count;
    public int BosId => _bos;
    public int EosId => _eos;

    public Tokenizer(string tokenizerJsonPath)
    {
        using var fs = File.OpenRead(tokenizerJsonPath);
        var root = JsonDocument.Parse(fs, new JsonDocumentOptions { MaxDepth = 64 }).RootElement;
        var model = root.GetProperty("model");

        _vocab = new Dictionary<string, int>(262144, StringComparer.Ordinal);
        foreach (var p in model.GetProperty("vocab").EnumerateObject())
            _vocab[p.Name] = p.Value.GetInt32();

        int rank = 0;
        foreach (var m in model.GetProperty("merges").EnumerateArray())
        {
            string a, b;
            if (m.ValueKind == JsonValueKind.Array)
            {
                a = m[0].GetString()!;
                b = m[1].GetString()!;
            }
            else
            {
                var s = m.GetString()!;
                int sp = s.IndexOf(' ');
                a = s.Substring(0, sp);
                b = s.Substring(sp + 1);
            }
            var key = a + "\u0000" + b;
            _merges.TryAdd(key, rank++);
        }

        var added = new List<(string, int)>();
        foreach (var t in root.GetProperty("added_tokens").EnumerateArray())
            added.Add((t.GetProperty("content").GetString()!, t.GetProperty("id").GetInt32()));
        added.Sort((a, b) => b.Item1.Length.CompareTo(a.Item1.Length));
        _added = added;

        _bos = _vocab.GetValueOrDefault("<bos>", 2);
        _eos = _vocab.GetValueOrDefault("<eos>", 1);
        _unk = _vocab.GetValueOrDefault("<unk>", 3);

        for (int b = 0; b < 256; b++)
        {
            var tok = $"<0x{b:X2}>";
            if (_vocab.TryGetValue(tok, out var id)) _byteTokens[tok] = id;
        }
    }

    public List<int> Encode(string text)
    {
        var ids = new List<int>(64);
        var plain = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            bool matched = false;
            foreach (var (content, id) in _added)
            {
                if (content.Length <= text.Length - i &&
                    string.CompareOrdinal(text, i, content, 0, content.Length) == 0)
                {
                    if (plain.Length > 0) { EncodePlain(plain.ToString(), ids); plain.Clear(); }
                    ids.Add(id);
                    i += content.Length;
                    matched = true;
                    break;
                }
            }
            if (!matched) { plain.Append(text[i]); i++; }
        }
        if (plain.Length > 0) EncodePlain(plain.ToString(), ids);

        var final = new List<int>(ids.Count + 2) { _bos };
        final.AddRange(ids);
        final.Add(_eos);
        return final;
    }

    private void EncodePlain(string segment, List<int> ids)
    {
        var norm = segment.Replace(' ', '\u2581');

        var syms = new List<string>();
        for (int i = 0; i < norm.Length;)
        {
            int len = char.IsSurrogatePair(norm, i) ? 2 : 1;
            syms.Add(norm.Substring(i, len));
            i += len;
        }

        while (syms.Count > 1)
        {
            int bestRank = int.MaxValue, bestIdx = -1;
            for (int i = 0; i < syms.Count - 1; i++)
            {
                if (_merges.TryGetValue(syms[i] + "\u0000" + syms[i + 1], out var r) && r < bestRank)
                {
                    bestRank = r;
                    bestIdx = i;
                }
            }
            if (bestIdx < 0) break;
            syms[bestIdx] = syms[bestIdx] + syms[bestIdx + 1];
            syms.RemoveAt(bestIdx + 1);
        }

        foreach (var s in syms)
        {
            if (_vocab.TryGetValue(s, out var id))
            {
                ids.Add(id);
                continue;
            }
            if (_byteTokens.Count == 256)
            {
                foreach (var b in Encoding.UTF8.GetBytes(s))
                    ids.Add(_byteTokens[$"<0x{b:X2}>"]);
            }
            else
            {
                ids.Add(_unk);
            }
        }
    }
}
