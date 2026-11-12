package daprmq

import (
	"context"
	"encoding/json"
	"testing"
	"time"

	"github.com/olitomlinson/dapr-mq/sdks/go/internal/pb"
	"google.golang.org/protobuf/proto"
)

// Message expiry (proposals/message-expiry.md): a time to live on enqueue and publish, and each
// delivered item's ExpiryAt, ExpiryTTL and OriginalExpiryTTL.

var wantExpiry = MessageExpiry{
	ExpiryAt:          time.Unix(1800003600, 0),
	ExpiryTTL:         time.Hour,
	OriginalExpiryTTL: 10 * time.Minute,
}

func withQueueExpiry(d *pb.ConsumeDelivered) {
	d.ExpiryAt, d.ExpiryTtlSeconds, d.OriginalExpiryTtlSeconds = proto.Float64(1800003600), proto.Int32(3600), proto.Int32(600)
}

func withSessionExpiry(d *pb.SessionDelivered) {
	d.ExpiryAt, d.ExpiryTtlSeconds, d.OriginalExpiryTtlSeconds = proto.Float64(1800003600), proto.Int32(3600), proto.Int32(600)
}

func sentItems(t *testing.T, body string) []map[string]any {
	t.Helper()
	var parsed struct {
		Items []map[string]any `json:"items"`
	}
	if err := json.Unmarshal([]byte(body), &parsed); err != nil {
		t.Fatal(err)
	}
	return parsed.Items
}

func TestEnqueueSendsExpiryTTLInWholeSecondsAndOmitsItWhenUnset(t *testing.T) {
	transport := newSequence(okEnqueue)

	if _, err := newTestClient(t, transport, fastRetry).Enqueue(bg, "q", []EnqueueItem{
		{Item: 1, ExpiryTTL: time.Hour},
		{Item: 2},
	}, nil); err != nil {
		t.Fatal(err)
	}

	items := sentItems(t, transport.bodies[0])
	if items[0]["expiryTtlSeconds"] != 3600.0 {
		t.Fatalf("first = %v", items[0])
	}
	if _, ok := items[1]["expiryTtlSeconds"]; ok {
		t.Fatalf("second = %v", items[1])
	}
}

func TestPublishSendsExpiryTTL(t *testing.T) {
	transport := newSequence(respond(202, map[string]any{"accepted": true, "publishId": "p1", "sequence": 0}, nil))

	if _, err := newTestClient(t, transport, fastRetry).Publish(bg, "t", []EnqueueItem{{Item: 1, ExpiryTTL: 10 * time.Minute}}, nil); err != nil {
		t.Fatal(err)
	}

	if items := sentItems(t, transport.bodies[0]); items[0]["expiryTtlSeconds"] != 600.0 {
		t.Fatalf("item = %v", items[0])
	}
}

func TestDequeueLockedReturnsEachItemsExpiry(t *testing.T) {
	transport := newSequence(ok(map[string]any{
		"items": []any{
			map[string]any{"item": 1, "priority": 1, "lockId": "L1", "lockExpiresAt": 1.0,
				"expiryAt": 1800003600.0, "expiryTtlSeconds": 3600, "originalExpiryTtlSeconds": 600},
			map[string]any{"item": 2, "priority": 1, "lockId": "L2", "lockExpiresAt": 1.0},
		},
	}))

	result, err := newTestClient(t, transport, fastRetry).DequeueLocked(bg, "q", &DequeueLockedOptions{Count: 2})
	if err != nil {
		t.Fatal(err)
	}

	if result.Items[0].MessageExpiry != wantExpiry {
		t.Fatalf("first = %+v", result.Items[0].MessageExpiry)
	}
	if result.Items[1].MessageExpiry != (MessageExpiry{}) {
		t.Fatalf("second = %+v", result.Items[1].MessageExpiry)
	}
}

func TestConsumeDeliveriesCarryTheirExpiry(t *testing.T) {
	script := newQueueScript()
	withTTL := queueDelivered("L1", `{}`, 1)
	withQueueExpiry(withTTL.GetDelivered())
	client := newGRPCClient(t, &fakeServer{consumeQueue: script.serve(withTTL, queueDelivered("L2", `{}`, 1))}, nil)

	stream, err := client.Consume(bg, "q", nil)
	if err != nil {
		t.Fatal(err)
	}
	defer stream.Close()

	d1, _ := stream.Receive()
	d2, _ := stream.Receive()
	if d1.MessageExpiry != wantExpiry {
		t.Fatalf("first = %+v", d1.MessageExpiry)
	}
	if d2.MessageExpiry != (MessageExpiry{}) {
		t.Fatalf("second = %+v", d2.MessageExpiry)
	}
}

func TestConsumeSessionDeliveriesCarryTheirExpiry(t *testing.T) {
	frame := delivered("L1", `{}`)
	withSessionExpiry(frame.GetDelivered())
	server := newSessionsServer([]*pb.ConsumeSessionResponse{assigned("s1"), frame})
	handled := make(chan SessionMessage, 1)
	consumer, _ := newConsumer(t, server, oneSlot(), func(_ context.Context, msg SessionMessage) error {
		handled <- msg
		return nil
	})
	consumer.Start(bg)

	if msg := <-handled; msg.MessageExpiry != wantExpiry {
		t.Fatalf("msg = %+v", msg.MessageExpiry)
	}
}

func TestQueueConsumerPassesTheMessagesExpiry(t *testing.T) {
	frame := queueDelivered("L1", `{}`, 1)
	withQueueExpiry(frame.GetDelivered())
	server := newQueueStreams(streamScript{frames: []*pb.ConsumeResponse{frame}})
	got := make(chan QueueMessage, 1)
	consumer, _ := newQueueConsumer(t, server, nil, func(_ context.Context, msg QueueMessage) error {
		got <- msg
		return nil
	})
	consumer.Start(bg)

	if msg := <-got; msg.MessageExpiry != wantExpiry {
		t.Fatalf("msg = %+v", msg.MessageExpiry)
	}
}
