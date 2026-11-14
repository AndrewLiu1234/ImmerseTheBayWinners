---
name: add-song
description: Convert an original, public-domain or chords-only chart the user provides into an Untitled Guitar App song.json and validate it. Refuses copyrighted lyrics and sheets.
argument-hint: "[path to the user's text/ChordPro file]"
disable-model-invocation: true
---
# Add song from $ARGUMENTS
**Person 2's lane** (songs JSON). If this clone works for Person 1, stop and pass the chart to Person 2 with a STATUS line.
1. Ask (one question) for the license: original | public-domain (needs a source URL) | chords-only (a copyrighted song: progression
   + credit, every lyric "") | user-provided (local only, never committed).
   Refuse lyrics/sheets of copyrighted songs or from Ultimate Guitar; offer a chords-only version of the progression instead.
2. Parse [Chord] markers → chords with "at" (character index); default 4 beats per chord in 4/4, 3 in 3/4; ask once if durations look off.
3. Chords must exist in Core/Theory/ChordLibrary; otherwise flag them and suggest a library chord or skipping the song.
4. Write unity/Assets/StreamingAssets/songs/<id>.song.json → ./scripts/verify.sh (AllSongsValid) → ask music-teacher to review.
