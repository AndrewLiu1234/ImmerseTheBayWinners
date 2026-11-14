---
name: record-fixtures
description: Human protocol for recording labelled guitar fixtures with the demo mic. Claude guides and prints labels.csv rows; humans commit the files.
disable-model-invocation: true
---
# Record fixtures (human-run, ~40 min)
Setup: guitar tuned with a reference tuner; seated as in the demo; the mic that won M-S3 (glasses or MacBook) at its demo
position; app Debug › Fixture Recorder (or any 48 kHz mono recorder on the Mac using the same device — note the source).
2–3 s per clip, start silent, no speech. Naming: <group>/<label>__<take>.wav · group = tuner | pluck-<chord> | strum-<chord> | noise.
1. tuner: each open string ×3 (18)
2. pluck-<chord>: every playable string of Em, G, C, D, Am played correctly ×2 (~54)
3. strum-<chord>: clean ×5 per chord (20) + mistakes ×3: Em {A-string finger missing; fingers one string off}, G {low-E finger
   missing; B muted}, C {A-string finger missing; low E strummed}, D {low E/A strummed; B-string fret 3 not pressed}, Am {B muted; low E strummed} (30)
4. noise: 60 s silence · 60 s room chatter. Repeat the strum set in the venue Sat 17:00 (env=venue).
Collect: ./scripts/pull-session.sh → logs/fixtures-raw/. A HUMAN moves files into core-tests/Fixtures/audio/ and appends the rows
Claude prints to labels.csv: file,group,chord,string,expected_midi,label,take,source,env (agents are blocked from Fixtures/).
