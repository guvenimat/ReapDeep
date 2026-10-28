#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

if [ ! -f .env ]; then
  rand() { head -c 16 /dev/urandom | od -An -tx1 | tr -d ' \n'; }
  printf 'POSTGRES_PASSWORD=%s\nREAD_API_KEY=rk_%s\n' "$(rand)" "$(rand)" > .env
  echo "Created .env with random local credentials"
fi

EVENT_ID="smoke-$(date +%s)"
NOW="$(date -u '+%Y-%m-%d %H:%M:%S')"
MSG="{\"project_id\":1,\"event\":\"smoke_test\",\"timestamp\":\"$NOW\",\"distinct_id\":\"anon_smoke\",\"event_id\":\"$EVENT_ID\",\"session_id\":null,\"ip\":\"127.0.0.1\",\"properties\":{\"\$lib\":\"smoke\"},\"inserted_at\":\"$NOW\"}"

echo "1) Writing to Kafka: $EVENT_ID"
echo "$MSG" | docker compose exec -T kafka /opt/kafka/bin/kafka-console-producer.sh \
  --bootstrap-server localhost:29092 --topic events

echo "2) Waiting for StarRocks (up to 60s)..."
for i in $(seq 1 12); do
  COUNT=$(docker compose exec -T starrocks mysql -h127.0.0.1 -P9030 -uroot -N -e \
    "SELECT count(*) FROM analytics.events WHERE event_id = '$EVENT_ID'" 2>/dev/null | tr -d '[:space:]')
  if [ "${COUNT:-0}" = "1" ]; then
    echo "PASSED: event queryable within $((i*5))s."
    exit 0
  fi
  sleep 5
done

echo "FAILED: event not visible within 60s. Routine Load status:"
docker compose exec -T starrocks mysql -h127.0.0.1 -P9030 -uroot -e \
  "SHOW ROUTINE LOAD FROM analytics\G" | grep -E "State|ReasonOfStateChanged|ErrorLogUrls|OtherMsg"
exit 1
