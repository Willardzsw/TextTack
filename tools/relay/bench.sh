#!/usr/bin/env bash
# Times real translations through tt-relay with a temporary TT code, then removes the code.
# Run on the server as root:  bash bench.sh [rounds]
# Each round sends one short sentence to every model; costs a fraction of a fen.
set -euo pipefail
ROUNDS="${1:-5}"
R=/opt/tt-relay/tt_relay.py
NAME="临时测速"
python3 $R remove-friend "$NAME" >/dev/null 2>&1 || true
CODE="$(python3 $R add-friend "$NAME" | tail -1)"
trap 'python3 $R remove-friend "$NAME" >/dev/null 2>&1 || true' EXIT
SINCE="$(date '+%Y-%m-%d %H:%M:%S')"

for i in $(seq 1 "$ROUNDS"); do
  for m in gpt-6-luna deepseek-flash; do
    curl -s -o /dev/null -m 60 -w "$m round $i: HTTP %{http_code}, client total %{time_total}s\n" \
      https://tt.allplusthailand.top/v1/chat/completions \
      -H "Authorization: Bearer $CODE" -H "Content-Type: application/json" \
      -d "{\"model\":\"$m\",\"messages\":[{\"role\":\"system\",\"content\":\"Translate the user text into Thai. Reply with the translation only.\"},{\"role\":\"user\",\"content\":\"Round $i: the machine is ready.\"}],\"max_completion_tokens\":100}"
    sleep 2
  done
done

echo "--- relay log (time spent inside the relay, upstream included)"
journalctl -u tt-relay --since "$SINCE" --no-pager -o cat | grep "^$NAME "
