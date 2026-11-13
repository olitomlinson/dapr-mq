package integration

import (
	"context"
	"testing"
	"time"

	daprmq "github.com/olitomlinson/dapr-mq/sdks/go"
)

// E-01..E-03 from sdks/testing/INTEGRATION_TESTS.md: message expiry. The stack sets
// DAPRMQ_MIN_EXPIRY_TTL_SECONDS to 1 so an item can expire within a test.

// Server default, DAPRMQ_DEADLETTER_EXPIRY_TTL_SECONDS unset.
const deadLetterExpiryTTL = 28 * 24 * time.Hour

func TestE01_Expired_Item_Is_Not_Delivered(t *testing.T) {
	client := newClient(t, nil)
	ctx := context.Background()
	queueID := newQueueID()

	if _, err := client.Enqueue(ctx, queueID, []daprmq.EnqueueItem{{Item: map[string]int{"seq": 1}, ExpiryTTL: time.Second}}, nil); err != nil {
		t.Fatal(err)
	}
	time.Sleep(2500 * time.Millisecond)

	result, err := client.DequeueLocked(ctx, queueID, nil)
	if err != nil || len(result.Items) != 0 {
		t.Fatalf("result=%+v err=%v", result, err)
	}
}

func TestE02_Dequeued_And_Consumed_Items_Carry_Their_Expiry(t *testing.T) {
	client := newClient(t, nil)
	ctx := context.Background()
	queueID := newQueueID()
	before := time.Now()

	if _, err := client.Enqueue(ctx, queueID, []daprmq.EnqueueItem{
		{Item: map[string]int{"seq": 1}, ExpiryTTL: time.Hour},
		{Item: map[string]int{"seq": 2}, ExpiryTTL: 2 * time.Hour},
	}, nil); err != nil {
		t.Fatal(err)
	}

	result, err := client.DequeueLocked(ctx, queueID, nil)
	if err != nil {
		t.Fatal(err)
	}
	locked := result.Items[0]
	if locked.ExpiryTTL != time.Hour || !near(locked.ExpiryAt, before.Add(time.Hour)) || locked.OriginalExpiryTTL != 0 {
		t.Fatalf("locked = %+v", locked.MessageExpiry)
	}
	if err := client.Acknowledge(ctx, queueID, locked.LockID, nil); err != nil {
		t.Fatal(err)
	}

	stream, err := client.Consume(ctx, queueID, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer stream.Close()
	delivery, err := stream.Receive()
	if err != nil {
		t.Fatal(err)
	}
	if delivery.ExpiryTTL != 2*time.Hour || !near(delivery.ExpiryAt, before.Add(2*time.Hour)) {
		t.Fatalf("delivery = %+v", delivery.MessageExpiry)
	}
	_ = delivery.Ack()
}

func TestE03_Dead_Lettered_Item_Carries_The_Original_Time_To_Live(t *testing.T) {
	client := newClient(t, nil)
	ctx := context.Background()
	queueID := newQueueID()

	if _, err := client.Enqueue(ctx, queueID, []daprmq.EnqueueItem{{Item: map[string]int{"seq": 1}, ExpiryTTL: time.Hour}}, nil); err != nil {
		t.Fatal(err)
	}
	result, err := client.DequeueLocked(ctx, queueID, nil)
	if err != nil {
		t.Fatal(err)
	}
	if err := client.DeadLetter(ctx, queueID, result.Items[0].LockID, nil); err != nil {
		t.Fatal(err)
	}

	dead, err := client.DequeueLocked(ctx, queueID+"-deadletter", nil)
	if err != nil || len(dead.Items) != 1 {
		t.Fatalf("dead=%+v err=%v", dead, err)
	}
	if dead.Items[0].ExpiryTTL != deadLetterExpiryTTL || dead.Items[0].OriginalExpiryTTL != time.Hour {
		t.Fatalf("dead = %+v", dead.Items[0].MessageExpiry)
	}
}

// near allows for the API server stamping the expiry slightly after the test read its clock.
func near(got, want time.Time) bool {
	return got.After(want.Add(-5*time.Second)) && got.Before(want.Add(30*time.Second))
}
