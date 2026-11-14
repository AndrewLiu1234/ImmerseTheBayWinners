---
paths:
  - "unity/Assets/UntitledGuitarApp/Runtime/**"
  - "unity/Assets/UntitledGuitarApp/Editor/**"
  - "unity/Assets/UntitledGuitarApp/Tests/**"
  - "unity/Assets/StreamingAssets/poses/**"
  - "tools/imu-bridge/**"
---
# Glasses + Unity (Mac route)
- The glasses are display 2 (SBS 3840×1080 in 3D mode), an IMU (tools/imu-bridge → UDP) and mics/speakers. Nothing else:
  no XREAL SDK, no cameras, no 6DoF, no hand/image tracking.
- Packet layouts and Unity display APIs come from docs/apis/** — cite them; never guess byte offsets.
- Serialized assets only via Unity MCP or Editor scripts; scene composition in Bootstrap code.
- Unity APIs on the main thread; audio and IMU threads hand data over through queues/slots; no per-frame allocations.
- Additive display: no dark UI; bold emissive text ≥1.2°; ghost hands = glowing translucent blue (rim + low fill,
  docs/GHOST_HANDS.md); fade the fretting ghost to 40% once audio confirms the chord.
- Every risky feature has a flag with a safe default; headLocked is the universal fallback; SIMULATED badge when audioSource = wav.
- Keep EditorSimRig + WAV replay working (L4).
- Lane: Person 1 (XR). Person 2's agents may read these files but never edit them (hook-enforced; docs/WORKSPLIT.md).
