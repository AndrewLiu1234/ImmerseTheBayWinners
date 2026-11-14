#!/usr/bin/env bash
# L2: recorded-fixture evaluation. AudioEval tests print the table and fail on >3-point regressions vs docs/AUDIO_BASELINE.md.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
dotnet test core-tests -c Release --nologo --filter "Category=AudioEval" --logger "console;verbosity=normal"
