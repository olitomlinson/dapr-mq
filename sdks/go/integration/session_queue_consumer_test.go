package integration

import (
	"context"
	"encoding/json"
	"slices"
	"sync"
	"testing"
	"time"

	daprmq "github.com/olitomlinson/dapr-mq/sdks/go"
)

const (
	itemsPerSession    = 5
	slowHandlerSeconds = 300 * time.Millisecond
)

func TestK02_MultiSession_Preserves_Per_Session_Order_And_Isolates_Throughput(t *testing.T) {
	client := newClient(t, nil)
	queueID := newQueueID()
	ctx := context.Background()
	for seq := 1; seq <= itemsPerSession; seq++ {
		for _, sessionID := range []string{"fast", "slow"} {
			if _, err := client.Enqueue(ctx, queueID, []daprmq.EnqueueItem{{
				Item: map[string]any{"sessionId": sessionID, "seq": seq}, SessionID: sessionID,
			}}, nil); err != nil {
				t.Fatal(err)
			}
		}
	}

	var mu sync.Mutex
	observed := map[string][]int{}
	done := map[string]chan struct{}{"fast": make(chan struct{}), "slow": make(chan struct{})}
	var fastCompletedAfter time.Duration
	started := time.Now()

	handler := func(ctx context.Context, msg daprmq.SessionMessage) error {
		if msg.SessionID == "slow" {
			time.Sleep(slowHandlerSeconds)
		}
		var item struct {
			Seq int `json:"seq"`
		}
		if err := json.Unmarshal(msg.Item, &item); err != nil {
			return err
		}
		mu.Lock()
		defer mu.Unlock()
		observed[msg.SessionID] = append(observed[msg.SessionID], item.Seq)
		if len(observed[msg.SessionID]) == itemsPerSession {
			if msg.SessionID == "fast" {
				fastCompletedAfter = time.Since(started)
			}
			close(done[msg.SessionID])
		}
		return nil
	}

	consumer, err := daprmq.NewSessionQueueConsumer(client, queueID, handler, &daprmq.SessionQueueConsumerOptions{
		MaxConcurrentSessions: 2,
		LeaseDuration:         30 * time.Second,
		PrefetchCount:         10,
		MinBackoff:            time.Second,
		MaxBackoff:            2 * time.Second,
	})
	if err != nil {
		t.Fatal(err)
	}
	consumer.Start(ctx)
	defer consumer.Stop(ctx)

	timeout := time.After(30 * time.Second)
	for _, sessionID := range []string{"fast", "slow"} {
		select {
		case <-done[sessionID]:
		case <-timeout:
			t.Fatalf("timed out; observed %v", observed)
		}
	}

	mu.Lock()
	defer mu.Unlock()
	expected := []int{1, 2, 3, 4, 5}
	if !slices.Equal(observed["fast"], expected) || !slices.Equal(observed["slow"], expected) {
		t.Fatalf("observed %v", observed)
	}
	// "slow" needs at least itemsPerSession * 0.3 s (sequential within its own stream); "fast"
	// must finish well inside that, proving the sessions run on independent streams and slots.
	if slowFloor := itemsPerSession * slowHandlerSeconds; fastCompletedAfter >= slowFloor {
		t.Fatalf("fast session took %s, expected well under slow's %s floor", fastCompletedAfter, slowFloor)
	}
}
