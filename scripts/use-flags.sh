#!/usr/bin/env bash
# Switch runtime behavior without rebuilding: ./scripts/use-flags.sh dev|demo|judge|safe  (the app hot-reloads within ~1 s)
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
PROFILE="${1:-dev}"
DEST="${UGA_DATA_DIR:-$HOME/Library/Application Support/UntitledGuitarApp/Untitled Guitar App}"
python3 -c 'import json,sys; json.load(open(sys.argv[1]))' "tools/flags/$PROFILE.json"   # fails loudly on bad JSON
mkdir -p "$DEST" && cp "tools/flags/$PROFILE.json" "$DEST/flags.json"
echo "Flags '$PROFILE' → $DEST/flags.json"
