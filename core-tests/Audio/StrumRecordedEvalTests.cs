using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Core.Tests.Audio;
using UntitledGuitarApp.Core.Theory;
using Xunit;
using Xunit.Abstractions;

// SL3 acceptance, recorded half (L2). (1) Our fixtures: strum clips in labels.csv (a chord, no string), every detected onset
// judged against the labelled chord; "clean…" labels must come out Clean, "fault…" labels must not. Metrics per env
// (strum_chord_acc_quiet / _venue, strum_false_clean) fail only when docs/AUDIO_BASELINE.md has the key and the metric drops
// > 3 points. (2) Open datasets in data/ (GUITAR CHORDS V3, GuitarSet): REPORT-ONLY, never gated. Nothing is downloaded.
[Trait("Category", "AudioEval")]
public class StrumRecordedEvalTests
{
    readonly ITestOutputHelper _o;
    public StrumRecordedEvalTests(ITestOutputHelper o) { _o = o; }
    void Log(string s) { _o.WriteLine(s); Console.WriteLine("[AudioEval] " + s); }

    static readonly Dictionary<string, IReadOnlyList<ChordShape>> Confusers =
        ChordLibrary.All.ToDictionary(c => c.Name, c => StrumVerifier.DefaultConfusers(c));

    /// <summary>Strum check of every onset in a clip (the whole clip if the detector finds none).</summary>
    static List<StrumCheckResult> JudgeClip(float[] x, int sr, ChordShape chord)
    {
        var onsets = new SpectralFluxOnset(AudioConfig.Default, sr);
        var v = new StrumVerifier(AudioConfig.Default, sr);
        var times = new List<double>();
        for (int i = 0; i < x.Length; i += 512) { onsets.Process(x, i, Math.Min(512, x.Length - i)); while (onsets.TryReadOnset(out double t)) times.Add(t); }
        if (times.Count == 0) times.Add(0);
        return times.Select(t => v.Verify(x, 0, x.Length, (int)Math.Round(t * sr), chord, Confusers[chord.Name])).ToList();
    }

    static bool IsStrumRow(LabelRow r) =>
        !string.IsNullOrWhiteSpace(r.Chord) && ChordLibrary.TryGet(r.Chord, out _) && r.StringNumber == 0 &&
        ((r.Group ?? "").ToLowerInvariant().Contains("chord") || (r.Group ?? "").ToLowerInvariant().Contains("strum") ||
         (r.Label ?? "").ToLowerInvariant().Contains("strum"));

    [Fact]
    public void RecordedStrums_ChordAccuracyAndFalseClean_Report()
    {
        var rows = FixtureLabels.Load().Where(IsStrumRow).ToList();
        if (rows.Count == 0) { Log("SL3 recorded strums: no fixtures yet (labels.csv has no chord-strum rows) — nothing to evaluate."); return; }

        var byEnv = new Dictionary<string, (int n, int ok)>();
        int faulty = 0, falseClean = 0;
        var confusion = new Dictionary<string, Dictionary<string, int>>();
        Log($"{"file",-44} {"env",-6} {"chord",-5} {"label",-18} strums  verdicts");
        foreach (var row in rows)
        {
            string path = FixtureLabels.Resolve(row.File);
            if (!File.Exists(path)) { Log($"{row.File,-44} MISSING FILE"); continue; }
            var x = WavReader.ReadMono(path, out int sr);
            var chord = ChordLibrary.Get(row.Chord);
            bool wantClean = (row.Label ?? "").ToLowerInvariant().StartsWith("clean");
            string env = string.IsNullOrEmpty(row.Env) ? "?" : row.Env.ToLowerInvariant();
            var res = JudgeClip(x, sr, chord);
            foreach (var r in res)
            {
                bool right = wantClean ? r.Clean : !r.Clean;
                var e = byEnv.TryGetValue(env, out var c) ? c : (n: 0, ok: 0);
                byEnv[env] = (e.n + 1, e.ok + (right ? 1 : 0));
                if (!wantClean) { faulty++; if (r.Clean) falseClean++; }
                if (!confusion.TryGetValue(chord.Name, out var m)) confusion[chord.Name] = m = new Dictionary<string, int>();
                string heard = r.BestMatch ?? "null";
                m[heard] = (m.TryGetValue(heard, out int k) ? k : 0) + 1;
            }
            Log($"{row.File,-44} {env,-6} {chord.Name,-5} {row.Label,-18} {res.Count,6}  " +
                string.Join(" | ", res.Select(r => r.Clean ? "CLEAN" : $"{r.Issue}{(r.IssueString > 0 ? " s" + r.IssueString : "")} ({r.BestMatch ?? "-"})")));
        }
        Log("confusion (labelled chord → best match): " + string.Join("  ", confusion.Select(kv => kv.Key + ": " + string.Join(",", kv.Value.Select(p => $"{p.Key}×{p.Value}")))));

        var baseline = FixtureLabels.LoadBaseline();
        foreach (var kv in byEnv)
        {
            string key = $"strum_chord_acc_{kv.Key}";
            double metric = 100.0 * kv.Value.ok / Math.Max(1, kv.Value.n);
            Log($"{key} = {metric:F1}% ({kv.Value.ok}/{kv.Value.n}) · MVP target {(kv.Key == "venue" ? 80 : 90)}%");
            if (baseline[key] != null) Assert.True(metric >= (double)baseline[key] - 3.0, $"{key} {metric:F1}% dropped > 3 points below baseline {(double)baseline[key]:F1}%");
        }
        if (faulty > 0)
        {
            double fc = 100.0 * falseClean / faulty;
            Log($"strum_false_clean = {fc:F1}% ({falseClean}/{faulty}) · MVP target ≤ 5%");
            if (baseline["strum_false_clean"] != null) Assert.True(fc <= (double)baseline["strum_false_clean"] + 3.0, $"strum_false_clean {fc:F1}% rose > 3 points");
        }
        if (byEnv.Keys.All(k => baseline[$"strum_chord_acc_{k}"] == null)) Log("no strum baseline yet — report only (a human adds it via /audio-eval).");
    }

    static string DataDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "docs", "AUDIO_BASELINE.md"))) return Path.Combine(d.FullName, "data");
        return null;
    }

    [Fact]
    public void OpenDatasets_StrumChordAccuracy_ReportOnly()
    {
        string data = DataDir();
        if (data == null || !Directory.Exists(data)) { Log("SL3 dataset report: datasets not present (no data/; ./scripts/get-datasets.sh fetches them) — skipped."); return; }

        // GUITAR CHORDS V3: one strummed chord per file; the chord name is a path token (folder or file-name prefix).
        var gc = Directory.EnumerateDirectories(data, "*", SearchOption.AllDirectories)
            .Where(d => { var n = Path.GetFileName(d).ToLowerInvariant(); return n.Contains("guitar") && n.Contains("chord"); }).ToList();
        if (gc.Count == 0) Log("GUITAR CHORDS V3: not present in data/.");
        else
        {
            var files = gc.SelectMany(d => Directory.EnumerateFiles(d, "*.wav", SearchOption.AllDirectories)).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();
            var perChord = new Dictionary<string, (int n, int heard, int clean)>();
            int skipped = 0;
            foreach (var f in files)
            {
                var tokens = f.Substring(data.Length).Split(new[] { '/', '\\', '_', '-', ' ', '.' }, StringSplitOptions.RemoveEmptyEntries);
                var chord = tokens.Select(t => ChordLibrary.TryGet(t, out var c) ? c : null).LastOrDefault(c => c != null);
                if (chord == null) { skipped++; continue; }
                float[] x;
                int sr;
                try { x = WavReader.ReadMono(f, out sr); } catch (Exception) { skipped++; continue; }
                if (sr < 8000) { skipped++; continue; }
                var r = JudgeClip(x, sr, chord).First();
                var e = perChord.TryGetValue(chord.Name, out var c0) ? c0 : (n: 0, heard: 0, clean: 0);
                perChord[chord.Name] = (e.n + 1, e.heard + (r.BestMatch == chord.Name ? 1 : 0), e.clean + (r.Clean ? 1 : 0));
            }
            foreach (var kv in perChord.OrderBy(k => k.Key))
                Log($"GUITAR CHORDS V3 {kv.Key,-3}: best match right {100.0 * kv.Value.heard / kv.Value.n:F1}% · clean {100.0 * kv.Value.clean / kv.Value.n:F1}% (n={kv.Value.n})");
            int n = perChord.Values.Sum(v => v.n);
            if (n > 0) Log($"GUITAR CHORDS V3 overall: best match right {100.0 * perChord.Values.Sum(v => v.heard) / n:F1}% (n={n}, {skipped} files skipped: chord not in our library or unreadable) — REPORT ONLY; their voicings may differ from our open shapes.");
        }

        bool guitarSet = Directory.EnumerateFileSystemEntries(data, "*", SearchOption.TopDirectoryOnly)
            .Any(p => Path.GetFileName(p).ToLowerInvariant().Contains("guitarset"));
        Log(guitarSet
            ? "GuitarSet: present, chord-level strum report not implemented — its comp excerpts use voicings (barre, inversions) the open-shape hypotheses don't model; needs a pitch-class-only hypothesis set (next step)."
            : "GuitarSet: not present in data/.");
    }
}
