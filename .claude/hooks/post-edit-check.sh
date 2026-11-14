#!/usr/bin/env bash
# Instant feedback: Core purity and JSON validity. Exit 2 = message back to Claude.
INPUT=$(cat)
if command -v jq >/dev/null 2>&1; then FILE=$(printf '%s' "$INPUT" | jq -r '.tool_input.file_path // empty')
elif command -v python3 >/dev/null 2>&1; then
  FILE=$(printf '%s' "$INPUT" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("tool_input",{}).get("file_path",""))')
else exit 0; fi
{ [ -z "$FILE" ] || [ ! -f "$FILE" ]; } && exit 0
case "$FILE" in
  */UntitledGuitarApp/Core/*.cs)
    if grep -nE '^[[:space:]]*using[[:space:]]+Unity(Engine|Editor)' "$FILE" >&2; then
      echo "Core purity: $FILE uses UnityEngine/UnityEditor. Move engine code to Runtime behind a Core contract." >&2
      exit 2
    fi ;;
  *.json)
    ok=y
    if command -v jq >/dev/null 2>&1; then jq empty "$FILE" >/dev/null 2>&1 || ok=n
    elif command -v python3 >/dev/null 2>&1; then python3 -c 'import json,sys; json.load(open(sys.argv[1]))' "$FILE" 2>/dev/null || ok=n
    fi
    [ "$ok" = y ] || { echo "Invalid JSON: $FILE" >&2; exit 2; } ;;
esac
exit 0
