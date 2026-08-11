#!/usr/bin/env bash
# Run the package's edit-mode tests under a given Unity editor.
#
#   scripts/run-tests.sh 6000.5.7f1      # current LTS — ExampleGame's native editor, runs in place
#   scripts/run-tests.sh 2021.3.45f1     # older editors run in a scratch DOWNGRADED copy of the
#                                        # project (older package versions, no ProjectVersion).
#                                        # NOTE: 2021.3 builds ≥ ~.46 are Extended-LTS and refuse
#                                        # to launch on a Personal license — use a pre-xLTS build.
#
# Results land in out/<version>/results.xml (+ unity.log).
set -euo pipefail

VERSION="${1:?usage: run-tests.sh <unity-version> (e.g. 6000.5.7f1)}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
UNITY="/Applications/Unity/Hub/Editor/$VERSION/Unity.app/Contents/MacOS/Unity"
[[ -x "$UNITY" ]] || { echo "Unity $VERSION not found at $UNITY" >&2; exit 1; }

OUT="$REPO/out/$VERSION"
mkdir -p "$OUT"

if [[ "$VERSION" == 6000.* ]]; then
  PROJECT="$REPO/ExampleGame"
else
  PROJECT="$OUT/testhost"
  mkdir -p "$PROJECT"
  rsync -a --delete \
    --exclude Library --exclude Temp --exclude Logs --exclude UserSettings \
    --exclude obj --exclude 'Build*' --exclude Packages/packages-lock.json \
    "$REPO/ExampleGame/" "$PROJECT/"
  # older editors want older package lines; the file: dep needs an absolute path from here
  sed -i '' \
    -e "s|file:../../com.brightmotion.agenthog|file:$REPO/com.brightmotion.agenthog|" \
    -e 's|"com.unity.test-framework": "1.4.5"|"com.unity.test-framework": "1.1.33"|' \
    -e 's|"com.unity.ugui": "2.0.0"|"com.unity.ugui": "1.0.0"|' \
    "$PROJECT/Packages/manifest.json"
  rm -f "$PROJECT/ProjectSettings/ProjectVersion.txt"
fi

echo "▶ Unity $VERSION editmode tests · project: $PROJECT"
set +e
"$UNITY" -batchmode -nographics \
  -projectPath "$PROJECT" \
  -runTests -testPlatform EditMode \
  -testResults "$OUT/results.xml" \
  -logFile "$OUT/unity.log"
CODE=$?
set -e

if [[ -f "$OUT/results.xml" ]]; then
  python3 - "$OUT/results.xml" <<'PY'
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
print(f"result={root.get('result')} total={root.get('total')} passed={root.get('passed')} "
      f"failed={root.get('failed')} skipped={root.get('skipped')}")
for case in root.iter('test-case'):
    if case.get('result') not in ('Passed', 'Skipped'):
        print(f"  FAIL {case.get('fullname')}")
        message = case.find('.//message')
        if message is not None and message.text:
            print('    ' + message.text.strip().replace('\n', '\n    ')[:2000])
PY
else
  echo "no results.xml produced — compile error? tail of unity.log:" >&2
  tail -40 "$OUT/unity.log" >&2
fi
exit $CODE
