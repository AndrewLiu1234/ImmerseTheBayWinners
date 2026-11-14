#!/usr/bin/env bash
# Headless EditMode test run: compiles unity/ and runs every EditMode test, with no human and no open editor.
# ONLY when the Unity editor does not have the project open (otherwise use Unity MCP refresh_unity -> read_console -> run_tests).
#
#   ./scripts/unity-tests.sh            # about 1-2 min on this Mac (a cold Library takes longer the first time)
#
# Output: build/editmode-results.xml (NUnit result) and build/unity-tests.log (Unity's log); both are gitignored.
# Prints one line "unity-tests: EditMode passed=N failed=N total=N ..." plus every `error CS` line from the log, then PASS or FAIL.
# Exit codes:  0 = every test passed AND no compile errors. Exception: a test that reports Inconclusive (it had nothing to
#                  measure yet, e.g. no pose files, or no per-thread allocation counter in Unity's Mono) does not fail the
#                  run when nothing failed and passed + inconclusive = total; each one is printed by name with its reason.
#              1 = a test failed, there were compile errors, nothing ran (0 tests / no result file), or Unity failed or timed out
#              2 = refused to start (the editor has this project open, Unity or the project is missing)
# Environment: UNITY_PATH (default: the pinned 2021.3.3f1 editor), UNITY_TESTS_TIMEOUT in seconds (default 1200).
# Memory: this script does not watch memory. Wrap it in a watchdog when other apps are heavy.
# bash 3.2 and BSD tools only. The result is never taken from a pipe's exit status: Unity's own exit code comes from `wait`,
# and the verdict comes from the parsed XML and the log text.
set -euo pipefail
cd "$(dirname "$0")/.."

UNITY_PATH="${UNITY_PATH:-/Applications/Unity/Hub/Editor/2021.3.3f1/Unity.app/Contents/MacOS/Unity}"
TIMEOUT="${UNITY_TESTS_TIMEOUT:-1200}"
PROJECT=unity
RESULTS=build/editmode-results.xml
LOG=build/unity-tests.log
LOCK="$PROJECT/Temp/UnityLockfile"

[ -x "$UNITY_PATH" ] || { echo "unity-tests: Unity not found at $UNITY_PATH (set UNITY_PATH)" >&2; exit 2; }
[ -f "$PROJECT/ProjectSettings/ProjectVersion.txt" ] || { echo "unity-tests: $PROJECT/ is not a Unity project yet (run ./scripts/init-unity.sh)" >&2; exit 2; }

# Unity keeps Temp/UnityLockfile open while an editor has the project open. After a crash or kill the file can stay behind
# with nobody holding it, so the file alone proves nothing: ask lsof who has it open.
if [ -e "$LOCK" ]; then
  holders=$(lsof -t "$LOCK" 2>/dev/null || true)
  if [ -n "$holders" ]; then
    echo "unity-tests: refusing to run: a Unity editor has $PROJECT/ open (pid $(printf '%s' "$holders" | tr '\n' ' ')). Close it, or use Unity MCP run_tests." >&2
    exit 2
  fi
  echo "unity-tests: note: $LOCK exists but no process holds it (stale); continuing" >&2
fi

mkdir -p build
rm -f "$RESULTS" "$LOG"   # a stale file from an earlier run must never be mistaken for this run's result

echo "unity-tests: running EditMode tests (timeout ${TIMEOUT}s); log: $LOG"
# No -quit: with -runTests the editor exits by itself when the run is over (-quit would end it before the tests start).
"$UNITY_PATH" -batchmode -projectPath "$PROJECT" -runTests -testPlatform EditMode \
  -testResults "$PWD/$RESULTS" -logFile "$PWD/$LOG" &
unity_pid=$!

timed_out=0
deadline=$(( $(date +%s) + TIMEOUT ))
while kill -0 "$unity_pid" 2>/dev/null; do
  if [ "$(date +%s)" -ge "$deadline" ]; then
    timed_out=1
    echo "unity-tests: TIMEOUT after ${TIMEOUT}s, stopping Unity (pid $unity_pid)" >&2
    pkill -TERM -P "$unity_pid" 2>/dev/null || true
    kill -TERM "$unity_pid" 2>/dev/null || true
    sleep 5
    pkill -KILL -P "$unity_pid" 2>/dev/null || true
    kill -KILL "$unity_pid" 2>/dev/null || true
    break
  fi
  sleep 1
done
unity_rc=0
wait "$unity_pid" || unity_rc=$?

# ---- compile errors from the log -------------------------------------------------------------------------------------
compile_errors=""
n_compile=0
if [ -f "$LOG" ]; then
  compile_errors=$(grep -E 'error CS[0-9]+' "$LOG" | sort -u | head -n 40 || true)
  if [ -n "$compile_errors" ]; then n_compile=$(printf '%s\n' "$compile_errors" | grep -c . || true); fi
fi

# ---- test counts from the NUnit XML ----------------------------------------------------------------------------------
total=""; passed=""; failed=""; skipped=""; inconclusive=""
if [ -s "$RESULTS" ]; then
  runtag=$(grep -m1 -o '<test-run [^>]*>' "$RESULTS" || true)
  attr() { printf '%s\n' "$runtag" | sed -n "s/.* $1=\"\([0-9][0-9]*\)\".*/\1/p"; }
  total=$(attr total); passed=$(attr passed); failed=$(attr failed)
  skipped=$(attr skipped); inconclusive=$(attr inconclusive)
fi

# ---- report ----------------------------------------------------------------------------------------------------------
if [ -n "$total" ] && [ -n "$passed" ] && [ -n "$failed" ]; then
  echo "unity-tests: EditMode passed=$passed failed=$failed total=$total (skipped=${skipped:-?} inconclusive=${inconclusive:-?}; unity exit $unity_rc)"
  if [ "$failed" -gt 0 ]; then
    # Each failed test case: its full name, then up to 8 lines of its own failure message (parent suites repeat a generic one; skipped).
    awk '
      function emit(s) { if (shown < 8 && s != "") { print "    " s; shown++ } }
      /<test-suite / { incase = 0; inmsg = 0 }
      /<test-case / {
        incase = ($0 ~ /result="Failed"/); inmsg = 0
        if (incase) { match($0, /fullname="[^"]*"/); print "  FAILED: " substr($0, RSTART + 10, RLENGTH - 11); shown = 0 }
        next
      }
      /<\/test-case>/ { incase = 0; inmsg = 0; next }
      incase && !inmsg && /<message><!\[CDATA\[/ {
        s = $0; sub(/.*<message><!\[CDATA\[/, "", s)
        if (s ~ /\]\]><\/message>/) { sub(/\]\]><\/message>.*/, "", s) } else { inmsg = 1 }
        emit(s); next
      }
      inmsg { s = $0; if (s ~ /\]\]><\/message>/) { sub(/\]\]><\/message>.*/, "", s); inmsg = 0 } emit(s) }
    ' "$RESULTS" || true
  fi
  if [ "${inconclusive:-0}" -gt 0 ]; then
    # Each inconclusive test case: its full name and the first line of its reason. Not a failure, but never silent.
    awk '
      /<test-case / {
        incase = ($0 ~ /result="Inconclusive"/)
        if (incase) { match($0, /fullname="[^"]*"/); printf "  INCONCLUSIVE: %s", substr($0, RSTART + 10, RLENGTH - 11); need = 1 }
        next
      }
      incase && need && /<message><!\[CDATA\[/ {
        s = $0; sub(/.*<message><!\[CDATA\[/, "", s); sub(/\]\]><\/message>.*/, "", s)
        print " -- " substr(s, 1, 160); need = 0; next
      }
      /<\/test-case>/ { if (incase && need) print ""; incase = 0; need = 0 }
    ' "$RESULTS" || true
  fi
else
  echo "unity-tests: EditMode passed=? failed=? total=? (no usable result file at $RESULTS; unity exit $unity_rc)"
fi
if [ "$n_compile" -gt 0 ]; then
  echo "unity-tests: $n_compile distinct compile error line(s) in $LOG:"
  printf '%s\n' "$compile_errors" | sed 's/^/  /'
fi

verdict=PASS
reason=""
if [ "$timed_out" -ne 0 ]; then verdict=FAIL; reason="timed out"; fi
if [ "$n_compile" -gt 0 ]; then verdict=FAIL; reason="${reason:-compile errors}"; fi
if [ -z "$total" ] || [ -z "$passed" ] || [ -z "$failed" ]; then
  verdict=FAIL; reason="${reason:-no usable result file}"
else
  if [ "$total" -lt 1 ]; then verdict=FAIL; reason="${reason:-zero tests ran}"; fi
  if [ "$failed" -gt 0 ]; then verdict=FAIL; reason="${reason:-$failed test(s) failed}"; fi
  if [ $(( passed + ${inconclusive:-0} )) -ne "$total" ]; then verdict=FAIL; reason="${reason:-not every test passed ($passed of $total, ${inconclusive:-0} inconclusive)}"; fi
fi
# Unity exits 2 when any test did not pass, inconclusive ones included. Accept that code only when the counts above prove
# the only non-passes are inconclusive tests; every other non-zero exit fails the run.
only_inconclusive=0
if [ -n "$total" ] && [ -n "$failed" ] && [ "$failed" -eq 0 ] && [ "${inconclusive:-0}" -gt 0 ] && \
   [ $(( passed + inconclusive )) -eq "$total" ]; then only_inconclusive=1; fi
if [ "$unity_rc" -ne 0 ] && ! { [ "$unity_rc" -eq 2 ] && [ "$only_inconclusive" -eq 1 ]; }; then
  verdict=FAIL; reason="${reason:-unity exited with $unity_rc}"
fi

if [ "$verdict" = PASS ]; then
  if [ "$only_inconclusive" -eq 1 ]; then
    echo "unity-tests: PASS (with $inconclusive inconclusive test(s) listed above: nothing failed, but they measured nothing)"
  else
    echo "unity-tests: PASS"
  fi
  exit 0
fi
echo "unity-tests: FAIL ($reason). Results: $RESULTS  Log: $LOG" >&2
if [ -z "$total" ] && [ "$n_compile" -eq 0 ] && [ -f "$LOG" ]; then
  echo "unity-tests: last lines of the log:" >&2
  tail -n 25 "$LOG" >&2 || true
fi
exit 1
