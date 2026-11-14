using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Core.Tests.Audio;
using UntitledGuitarApp.Core.Theory;
using Xunit;
using Xunit.Abstractions;

// SL1 acceptance (recorded half): open strings within ±10 cents. Fixtures are human-owned (core-tests/CLAUDE.md); with none
// labelled this passes with a message. Metric `tuner_open_within10c` (% of open-string clips within ±10 cents) fails the run
// only when docs/AUDIO_BASELINE.md has that key and the metric drops > 3 points; otherwise it reports.
[Trait("Category", "AudioEval")]
public class TunerRecordedEvalTests
{
    const string MetricKey = "tuner_open_within10c";
    const double AcceptanceCents = 10.0;     // SL1: within ±10 cents on recorded open strings
    const float MinConfidence = 0.8f;        // §19: pluck-check confidence gate, reused for "a trustworthy frame"
    const double RingingDbBelowPeak = 20.0;  // frames within 20 dB of the clip's loudest frame = the ringing note, not the tail

    readonly Report _out;
    public TunerRecordedEvalTests(ITestOutputHelper output) { _out = new Report(output); }

    // xUnit shows ITestOutputHelper text only for failing tests at the script's "normal" verbosity, so the table is also
    // mirrored to stdout, which the console logger prints for every test.
    sealed class Report
    {
        readonly ITestOutputHelper _o;
        public Report(ITestOutputHelper o) { _o = o; }
        public void WriteLine(string s) { _o.WriteLine(s); Console.WriteLine("[AudioEval] " + s); }
    }

    [Fact]
    public void RecordedOpenStrings_MedianPitch_WithinTenCents()
    {
        var rows = FixtureLabels.Load().Where(r => r.IsOpenString).ToList();
        if (rows.Count == 0)
        {
            _out.WriteLine("SL1 recorded open strings: no fixtures yet (labels.csv has no open-string rows) — nothing to evaluate.");
            return;
        }

        _out.WriteLine($"{"file",-44} {"env",-6} str exp  {"medianHz",9} {"cents",7} {"frames",6} {"oct%",5}  ok");
        int within = 0, evaluated = 0;
        var worstByEnv = new Dictionary<string, double>();
        foreach (var row in rows)
        {
            string path = FixtureLabels.Resolve(row.File);
            if (!File.Exists(path)) { _out.WriteLine($"{row.File,-44} MISSING FILE"); evaluated++; continue; }
            var x = WavReader.ReadMono(path, out int sr);
            double expectedHz = Midi.ToHz(row.Midi);
            var hz = TrustedFrames(x, sr, out int total);
            evaluated++;
            if (hz.Count == 0) { _out.WriteLine($"{row.File,-44} {row.Env,-6} {row.StringNumber,3} {row.Midi,3}  no pitched frames ({total} analysed)  ✗"); continue; }

            hz.Sort();
            double median = hz[hz.Count / 2];
            double cents = Midi.CentsBetween(expectedHz, median);
            double octPct = 100.0 * hz.Count(h => Math.Abs(Midi.CentsBetween(expectedHz, h)) > 600) / hz.Count;
            bool ok = Math.Abs(cents) <= AcceptanceCents;
            if (ok) within++;
            string env = string.IsNullOrEmpty(row.Env) ? "?" : row.Env;
            worstByEnv[env] = Math.Max(worstByEnv.TryGetValue(env, out double w) ? w : 0, Math.Abs(cents));
            _out.WriteLine($"{row.File,-44} {env,-6} {row.StringNumber,3} {row.Midi,3}  {median,9:F2} {cents,7:F1} {hz.Count,6} {octPct,5:F1}  {(ok ? "✓" : "✗")}");
        }

        double metric = 100.0 * within / evaluated;
        foreach (var kv in worstByEnv) _out.WriteLine($"max |cents| [{kv.Key}] = {kv.Value:F1}");
        _out.WriteLine($"{MetricKey} = {metric:F1}% ({within}/{evaluated}) · SL1 acceptance (every clip ±{AcceptanceCents} c): {(within == evaluated ? "PASS" : "FAIL")}");

        var baseline = FixtureLabels.LoadBaseline();
        if (baseline[MetricKey] == null) { _out.WriteLine($"no baseline for {MetricKey} yet — report only (a human adds it via /audio-eval)."); return; }
        double b = (double)baseline[MetricKey];
        Assert.True(metric >= b - 3.0, $"{MetricKey} {metric:F1}% dropped more than 3 points below baseline {b:F1}%");
    }

    // Pitch of every hop whose frame is confidently pitched and still ringing (within RingingDbBelowPeak of the clip's peak).
    internal static List<double> TrustedFrames(float[] x, int sr, out int total)
    {
        var cfg = AudioConfig.Default;
        var t = new YinPitchTracker(cfg, sr);
        var results = new List<PitchResult>();
        for (int off = 0; off + cfg.FrameSize <= x.Length; off += cfg.HopSize) results.Add(t.Process(x, off, cfg.FrameSize));
        total = results.Count;
        float peak = results.Count > 0 ? results.Max(r => r.Rms) : 0f;
        float gate = peak * (float)Math.Pow(10, -RingingDbBelowPeak / 20);
        return results.Where(r => r.HasPitch && r.Confidence >= MinConfidence && r.Rms >= gate).Select(r => r.Hz).ToList();
    }
}
