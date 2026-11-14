---
paths:
  - "unity/Assets/UntitledGuitarApp/Core/**/*.cs"
---
# Core purity
- No UnityEngine/UnityEditor, no System.Threading in practice logic, no DateTime.Now — time is passed in (nowSec).
- No allocations in per-hop DSP paths: preallocate in constructors; use float[] + offsets.
- Avoid `record`/`init` (they need a polyfill in Unity); use sealed classes and readonly structs.
- Every tunable number lives in a config object (AudioConfig etc.) with a one-line comment saying why.
- Deterministic: same samples in → same verdicts out. Silence/noise never yields NaN/Inf or an exception.
- Contracts/** belongs to the architect: propose changes, don't make them.
- Lane: Person 2 (Core), except Contracts/** (the seam: both people OK). Person 1's agents may read these files but never edit them (hook-enforced; docs/WORKSPLIT.md).
