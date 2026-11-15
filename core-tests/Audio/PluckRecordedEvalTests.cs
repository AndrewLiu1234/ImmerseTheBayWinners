using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Core.Tests.Audio;
using UntitledGuitarApp.Core.Theory;
using Xunit;
using Xunit.Abstractions;

// SL2 acceptance, recorded half (L2): one pluck per clip, one string of a chord shape, judged against the fretted note.
// Metric pluck_correct_<env> fails only when docs/AUDIO_BASELINE.md has the key and the metric drops > 3 points; otherwise report only.
[Trait("Category", "AudioEval")]
public class PluckRecordedEvalTests
{
    readonly ITestOutputHelper _o;
    public PluckRecordedEvalTests(ITestOutputHelper o) { _o = o; }
    void Log(string s) { _o.WriteLine(s); Console.WriteLine("[AudioEval] " + s); }

    /// <summary>A fretted pluck: group says pluck, a known chord, a string 1-6 and an expected note. Disjoint from the tuner selector.</summary>
    public static bool IsPluckRow(LabelRow r) =>
        (r.Group ?? "").ToLowerInvariant().Contains("pluck") && !string.IsNullOrWhiteSpace(r.Chord) &&
        ChordLibrary.TryGet(r.Chord, out _) && r.StringNumber >= 1 && r.StringNumber <= Tuning.StringCount && r.Midi >= 0;

    [Fact]
    public void RowSelector_PicksPluckRowsAndNotTunerRows()
    {
        var rows = FixtureLabels.Parse(
            "file,group,chord,string,expected_midi,label,take,source,env\n" +
            "a.wav,pluck-G,G,3,55,pluck-s3,01,x,quiet\n" +
            "b.wav,tuner,,1,64,open-1-E4-quiet,01,x,quiet\n" +
            "c.wav,chord,G,,,clean,01,x,quiet\n");
        Assert.Equal(new[] { "a.wav" }, rows.Where(IsPluckRow).Select(r => r.File).ToArray());
        Assert.DoesNotContain(rows.Where(IsPluckRow), r => r.IsOpenString);
    }

    static void Add<K>(Dictionary<K, (int n, int ok)> d, K k, bool ok)
    {
        var e = d.TryGetValue(k, out var c) ? c : (n: 0, ok: 0);
        d[k] = (e.n + 1, e.ok + (ok ? 1 : 0));
    }

    [Fact]
    public void RecordedPlucks_Report()
    {
        var rows = FixtureLabels.Load().Where(IsPluckRow).ToList();
        if (rows.Count == 0) { Log("SL2 recorded plucks: no fixtures yet — nothing to evaluate."); return; }

        var byEnv = new Dictionary<string, (int n, int ok)>();
        var byChord = new Dictionary<string, (int n, int ok)>();
        var byString = new Dictionary<int, (int n, int ok)>();
        var verdicts = new Dictionary<string, int>();
        Log($"{"file",-34} {"chord",-5} {"str",3} {"midi",4} {"heardHz",8} {"cents",7} {"onset",6}  verdict");
        foreach (var row in rows)
        {
            string path = FixtureLabels.Resolve(row.File);
            if (!File.Exists(path)) { Log($"{row.File,-34} MISSING FILE"); continue; }
            var x = WavReader.ReadMono(path, out int sr);
            var chord = ChordLibrary.Get(row.Chord);
            string env = string.IsNullOrEmpty(row.Env) ? "?" : row.Env.ToLowerInvariant();

            var det = new SpectralFluxOnset(AudioConfig.Default, sr);
            double onsetT = -1;
            for (int i = 0; i < x.Length && onsetT < 0; i += 512)
            {
                det.Process(x, i, Math.Min(512, x.Length - i));
                if (det.TryReadOnset(out double t)) onsetT = t;
            }
            int onset = onsetT >= 0 ? (int)Math.Round(onsetT * sr) : 0;
            var r = new PluckVerifier(AudioConfig.Default, sr).Verify(x, 0, x.Length, onset, row.StringNumber, row.Midi, chord);

            bool ok = r.Verdict == PluckVerdict.Correct;
            Add(byEnv, env, ok); Add(byChord, chord.Name, ok); Add(byString, row.StringNumber, ok);
            string vk = r.Verdict.ToString();
            verdicts[vk] = (verdicts.TryGetValue(vk, out int k0) ? k0 : 0) + 1;
            string extra = r.HeardMidi >= 0 && r.HeardMidi != row.Midi ? $" (heard midi {r.HeardMidi} s{r.HeardString})" : "";
            Log($"{row.File,-34} {chord.Name,-5} {row.StringNumber,3} {row.Midi,4} {r.DetectedHz,8:F1} {r.Cents,7:F1} {(onsetT >= 0 ? onsetT.ToString("F2") : "none"),6}  {r.Verdict}{extra}");
        }
        Log("per chord:  " + string.Join("  ", byChord.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value.ok}/{k.Value.n}")));
        Log("per string: " + string.Join("  ", byString.OrderBy(k => k.Key).Select(k => $"s{k.Key} {k.Value.ok}/{k.Value.n}")));
        Log("verdicts:   " + string.Join("  ", verdicts.OrderByDescending(k => k.Value).Select(k => $"{k.Key}x{k.Value}")));

        var baseline = FixtureLabels.LoadBaseline();
        bool gated = false;
        foreach (var kv in byEnv)
        {
            string key = $"pluck_correct_{kv.Key}";
            double metric = 100.0 * kv.Value.ok / Math.Max(1, kv.Value.n);
            Log($"{key} = {metric:F1}% ({kv.Value.ok}/{kv.Value.n})");
            if (baseline[key] != null)
            {
                gated = true;
                Assert.True(metric >= (double)baseline[key] - 3.0, $"{key} {metric:F1}% dropped > 3 points below baseline {(double)baseline[key]:F1}%");
            }
        }
        if (!gated) Log("no pluck baseline yet — report only (a human adds it via /audio-eval).");
    }
}
