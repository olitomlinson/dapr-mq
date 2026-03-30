#!/bin/bash

QUEUE_ID="${1:-test-queue}"
HOST="${2:-http://localhost:3500}"

# Build JSON payload with 101 messages
cat <<EOF | curl -X POST "${HOST}/queue/${QUEUE_ID}/push" \
  -H "Content-Type: application/json" \
  -d @-
{
  "Items": [
$(for i in {1..1000}; do
  echo "    {\"Item\": {\"id\": $i, \"message\": \"Message $i\"}, \"Priority\": 1}$([ $i -lt 1000 ] && echo ',')"
done)
  ]
}
EOF
