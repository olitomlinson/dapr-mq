package integration

// R-01, R-04 and R-05 from sdks/testing/RETRIES_AND_READINESS.md against a real server.
// (R-02/R-03 need a split gateway/worker stack, which only the .NET fixture builds today.)

import (
	"context"
	"errors"
	"testing"
	"time"

	daprmq "github.com/olitomlinson/dapr-mq/sdks/go"
)

func TestR01_WaitForReady_Returns_And_An_Enqueue_Then_Succeeds(t *testing.T) {
	client := newClient(t, nil)
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()

	if err := client.WaitForReady(ctx, nil); err != nil {
		t.Fatal(err)
	}

	result, err := client.Enqueue(ctx, newQueueID(), []daprmq.EnqueueItem{{Item: map[string]int{"seq": 1}}}, nil)
	if err != nil || result.ItemsEnqueued != 1 {
		t.Fatalf("result=%+v err=%v", result, err)
	}
}

func TestR04_Server_Unreachable_Returns_Unavailable_Not_A_Hang(t *testing.T) {
	// Port 1 on loopback: nothing listens there, so connections are refused immediately.
	unreachable, err := daprmq.NewClient("http://127.0.0.1:1", "127.0.0.1:1", &daprmq.ClientOptions{
		Retry: daprmq.RetryOptions{Timeout: 2 * time.Second},
	})
	if err != nil {
		t.Fatal(err)
	}
	defer unreachable.Close()

	started := time.Now()
	_, err = unreachable.Enqueue(context.Background(), newQueueID(), []daprmq.EnqueueItem{{Item: 1}}, nil)

	var mqErr *daprmq.Error
	if !errors.As(err, &mqErr) || mqErr.Code != daprmq.CodeUnavailable || mqErr.Operation != "Enqueue" {
		t.Fatalf("want CodeUnavailable from Enqueue, got %v", err)
	}
	if time.Since(started) > 10*time.Second {
		t.Fatalf("took %s", time.Since(started))
	}
}

func TestR05_AutoIdempotencyKeys_Fill_Missing_Keys_And_Keep_Given_Ones(t *testing.T) {
	client := newClient(t, &daprmq.ClientOptions{Retry: daprmq.RetryOptions{AutoIdempotencyKeys: true}})
	queueID := newQueueID()
	items := []daprmq.EnqueueItem{
		{Item: map[string]int{"seq": 1}, IdempotencyKey: "mine-" + newQueueID()},
		{Item: map[string]int{"seq": 2}},
	}

	if _, err := client.Enqueue(context.Background(), queueID, items, nil); err != nil {
		t.Fatal(err)
	}
	second, err := client.Enqueue(context.Background(), queueID, items, nil)
	if err != nil {
		t.Fatal(err)
	}

	// The caller's key is kept (the repeat is de-duplicated); the unkeyed item gets a fresh key per call.
	if second.ItemsDeduplicated != 1 || second.ItemsEnqueued != 1 {
		t.Fatalf("second = %+v", second)
	}
}
