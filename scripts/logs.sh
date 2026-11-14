#!/usr/bin/env bash
# Keep UGA lines and errors from Unity's log in logs/. Default = Editor log; pass the Player log path for builds:
#   ./scripts/logs.sh "$HOME/Library/Logs/UntitledGuitarApp/Untitled Guitar App/Player.log"
mkdir -p logs; F="logs/device-$(date +%Y%m%d-%H%M%S).log"
SRC="${1:-$HOME/Library/Logs/Unity/Editor.log}"
echo "Following $SRC → $F (Ctrl+C to stop)"
tail -n 0 -F "$SRC" | grep --line-buffered -E '^\[UGA\]|Exception|Error' | tee "$F"
