#!/usr/bin/env bash
# Copy session recordings and raw fixture recordings from the app's data folder into logs/.
set -uo pipefail
cd "$(git rev-parse --show-toplevel)"
SRC="${UGA_DATA_DIR:-$HOME/Library/Application Support/UntitledGuitarApp/Untitled Guitar App}"
mkdir -p logs/sessions logs/fixtures-raw
cp -R "$SRC/sessions/." logs/sessions/ 2>/dev/null || true
cp -R "$SRC/fixtures/." logs/fixtures-raw/ 2>/dev/null || true
ls -lt logs/sessions | head
