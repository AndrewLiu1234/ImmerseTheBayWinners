---
paths:
  - "unity/Assets/StreamingAssets/songs/**"
---
# Songs
- license ∈ {"original", "public-domain" (+ "source" URL proving PD), "chords-only" (+ "credit"; every lyric must be "")}.
  "user-provided" songs are never committed.
- Never paste lyrics or chord sheets from Ultimate Guitar or any copyrighted song. Chords-only versions of a progression are fine.
- Schema v1; every chord has beats; lyric chords have "at" (character index). Only chords from Core/Theory/ChordLibrary.
- ./scripts/verify.sh runs AllSongsValid.
- Lane: Person 2 (Core). Person 1's agents may read these files but never edit them (hook-enforced; docs/WORKSPLIT.md).
