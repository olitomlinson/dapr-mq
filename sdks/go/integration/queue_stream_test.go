package integration

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"slices"
	"testing"
	"time"

	daprmq "github.com/olitomlinson/dapr-mq/sdks/go"
)

func enqueueSeqs(t *testing.T, client *daprmq.Client, queueID string, seqs ...int) {
	t.Helper()
	items := make([]daprmq.EnqueueItem, len(seqs))
	for i, s := range seqs {
		items[i] = daprmq.EnqueueItem{Item: map[string]int{"seq": s}}
	}
	if _, err := client.Enqueue(context.Background(), queueID, items, nil); err != nil {
		t.Fatal(err)
	}
}

func seqOf(t *testing.T, d *daprmq.QueueDelivery) int {
	t.Helper()
	var v struct{ Seq int }
	if err := json.Unmarshal(d.Item, &v); err != nil {
		t.Fatal(err)
	}
	return v.Seq
}

// drain closes the stream and waits for the server to end it.
func drain(t *testing.T, stream *daprmq.QueueStream) {
	t.Helper()
	_ = stream.Close()
	for {
		if _, err := stream.Receive(); err != nil {
			if !errors.Is(err, io.EOF) {
				t.Fatalf("stream ended with %v", err)
			}
			return
		}
	}
}

func TestQS01_Consume_Delivers_In_Order_And_Ack_Removes(t *testing.T) {
	client := newClient(t, nil)
	queueID := newQueueID()
	enqueueSeqs(t, client, queueID, 1, 2, 3)

	stream, err := client.Consume(context.Background(), queueID, &daprmq.ConsumeOptions{PrefetchCount: 2, AllowCompetingConsumers: true})
	if err != nil {
		t.Fatal(err)
	}
	var got []int
	for len(got) < 3 {
		d, err := stream.Receive()
		if err != nil {
			t.Fatal(err)
		}
		if d.DeliveryCount != 1 {
			t.Fatalf("delivery count = %d", d.DeliveryCount)
		}
		got = append(got, seqOf(t, d))
		if err := d.Ack(); err != nil {
			t.Fatal(err)
		}
	}
	drain(t, stream)

	if !slices.Equal(got, []int{1, 2, 3}) {
		t.Fatalf("order = %v", got)
	}
	if empty, err := client.DequeueLocked(context.Background(), queueID, nil); err != nil || len(empty.Items) != 0 {
		t.Fatalf("acknowledged items are gone: %+v %v", empty, err)
	}
}

func TestQS02_Consume_Nack_Redelivers_With_The_Next_Delivery_Count(t *testing.T) {
	client := newClient(t, nil)
	queueID := newQueueID()
	enqueueSeqs(t, client, queueID, 1)

	stream, err := client.Consume(context.Background(), queueID, nil)
	if err != nil {
		t.Fatal(err)
	}
	first, err := stream.Receive()
	if err != nil {
		t.Fatal(err)
	}
	_ = first.Nack()
	second, err := stream.Receive()
	if err != nil {
		t.Fatal(err)
	}
	_ = second.Ack()
	drain(t, stream)

	if seqOf(t, second) != 1 || first.DeliveryCount != 1 || second.DeliveryCount != 2 {
		t.Fatalf("first = %+v, second = %+v", first, second)
	}
}

func TestQS03_Consume_Keeps_A_Delivered_Item_Locked_Past_Its_TTL(t *testing.T) {
	client := newClient(t, nil)
	queueID := newQueueID()
	enqueueSeqs(t, client, queueID, 1)

	var settleFailures []string
	stream, err := client.Consume(context.Background(), queueID, &daprmq.ConsumeOptions{
		LockTTL: 2 * time.Second, AllowCompetingConsumers: true,
		OnSettleFailed: func(lockID string, _ error) { settleFailures = append(settleFailures, lockID) },
	})
	if err != nil {
		t.Fatal(err)
	}
	d, err := stream.Receive()
	if err != nil {
		t.Fatal(err)
	}
	time.Sleep(6 * time.Second)

	other, err := client.DequeueLocked(context.Background(), queueID, &daprmq.DequeueLockedOptions{AllowCompetingConsumers: true})
	if err != nil || len(other.Items) != 0 {
		t.Fatalf("the item is still locked to the stream: %+v %v", other, err)
	}
	_ = d.Ack()
	drain(t, stream)
	if len(settleFailures) != 0 {
		t.Fatalf("ack rejected: %v", settleFailures)
	}
}

func TestQS04_Closing_The_Stream_Returns_Unsettled_Items_Straight_Away(t *testing.T) {
	client := newClient(t, nil)
	queueID := newQueueID()
	enqueueSeqs(t, client, queueID, 1)

	stream, err := client.Consume(context.Background(), queueID, &daprmq.ConsumeOptions{LockTTL: 300 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := stream.Receive(); err != nil {
		t.Fatal(err)
	}
	drain(t, stream)

	back, err := client.DequeueLocked(context.Background(), queueID, nil)
	if err != nil || len(back.Items) != 1 {
		t.Fatalf("the unsettled item is back well inside its 300 s lock: %+v %v", back, err)
	}
}
