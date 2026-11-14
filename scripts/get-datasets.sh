#!/usr/bin/env bash
# Fetch open datasets for development and evaluation. Nothing here is committed (data/ is gitignored).
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p data && cd data
fetch() { [ -f "$2" ] && { echo "have $2"; return 0; }; echo "↓ $2"; curl -fL --retry 3 -o "$2.part" "$1" && mv "$2.part" "$2"; }
# GuitarSet (CC BY 4.0): mono mic audio + annotations
fetch "https://zenodo.org/records/3371780/files/audio_mono-mic.zip?download=1" guitarset_audio_mono-mic.zip
fetch "https://zenodo.org/records/3371780/files/annotation.zip?download=1"     guitarset_annotation.zip
# NSynth test split (CC BY 4.0): single notes, incl. acoustic guitar
fetch "http://download.magenta.tensorflow.org/datasets/nsynth/nsynth-test.jsonwav.tar.gz" nsynth-test.jsonwav.tar.gz
# GUITAR CHORDS V3 (Kaggle, CC BY-SA 4.0): needs the kaggle CLI and an API token
if command -v kaggle >/dev/null 2>&1; then kaggle datasets download -d fabianavinci/guitar-chords-v3 -p . || true
else echo "skip GUITAR CHORDS V3 (pip install kaggle + API token)"; fi
echo "Unzip what you need; credit each dataset in docs/CREDITS.md."
