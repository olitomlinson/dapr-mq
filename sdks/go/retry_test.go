package daprmq

// The shared retry contract (sdks/testing/RETRIES_AND_READINESS.md): retry what certainly wasn't
// delivered, retry an unknown outcome only for a fully keyed enqueue or a batch acknowledge,
// within the retry timeout.

import (
	"context"
	"encoding/json"
	"errors"
	"net"
	"net/http"
	"regexp"
	"strconv"
	"syscall"
	"testing"
	"time"
)

func TestNotDeliveredIsRetriedUntilItSucceedsForAnyOperation(t *testing.T) {
	transport := newSequence(notDelivered, notDelivered, ok(map[string]any{}))

	if err := newTestClient(t, transport, fastRetry).Acknowledge(context.Background(), "q", "L1", nil); err != nil {
		t.Fatal(err)
	}

	if transport.count() != 3 {
		t.Fatalf("want 3 attempts, got %d", transport.count())
	}
}

func TestNackNotDeliveredIsRetriedUntilItSucceeds(t *testing.T) {
	transport := newSequence(notDelivered, ok(map[string]any{"success": true, "deadLettered": false, "deliveryCount": 1}))

	result, err := newTestClient(t, transport, fastRetry).Nack(context.Background(), "q", "L1", nil)
	if err != nil {
		t.Fatal(err)
	}

	if transport.count() != 2 || result.DeadLettered || result.DeliveryCount != 1 {
		t.Fatalf("attempts=%d result=%+v", transport.count(), result)
	}
}

func TestNackUnknownIsNotRetriedAndNamesTheOperation(t *testing.T) {
	transport := newSequence(unknown)

	_, err := newTestClient(t, transport, fastRetry).Nack(context.Background(), "q", "L1", nil)

	e := requireCode(t, err, CodeDeliveryUnknown)
	if transport.count() != 1 || e.Operation != "Nack" {
		t.Fatalf("attempts=%d operation=%q", transport.count(), e.Operation)
	}
}

func TestAcknowledgeBatchUnknownIsRetriedBecauseResendingIsHarmless(t *testing.T) {
	// The retry reports LOCK_NOT_FOUND for locks the first attempt settled: after a retry that
	// outcome means "already acknowledged", and the SDK passes it through unchanged.
	transport := newSequence(unknown, ok(map[string]any{
		"success": true, "itemsAcknowledged": 0,
		"results": []any{map[string]any{"lockId": "L1", "outcome": "LOCK_NOT_FOUND"}},
	}))

	result, err := newTestClient(t, transport, fastRetry).AcknowledgeBatch(context.Background(), "q", []string{"L1"}, nil)
	if err != nil {
		t.Fatal(err)
	}

	if transport.count() != 2 || result.Results[0].Outcome != AcknowledgeOutcomeLockNotFound {
		t.Fatalf("attempts=%d result=%+v", transport.count(), result)
	}
}

func TestEveryAttemptSendsTheRemainingRetryTimeNotACallDeadline(t *testing.T) {
	transport := newSequence(okEnqueue)

	if _, err := newTestClient(t, transport, fastRetry).Enqueue(context.Background(), "q", []EnqueueItem{{Item: map[string]int{"n": 1}}}, nil); err != nil {
		t.Fatal(err)
	}

	sent, _ := strconv.Atoi(transport.requests[0].Header.Get("daprmq-retry-timeout"))
	if sent < 4000 || sent > 5000 {
		t.Fatalf("daprmq-retry-timeout = %d, want 4000..5000", sent)
	}
	if transport.requests[0].Header.Get("daprmq-timeout") != "" {
		t.Fatal("daprmq-timeout must not be sent")
	}
}

func TestASlowResponseOutlivesTheRetryTimeout(t *testing.T) {
	// e.g. queued behind thousands of calls on one busy queue: slow, but progressing.
	slow := func(req *http.Request) (*http.Response, error) {
		time.Sleep(300 * time.Millisecond)
		return okEnqueue(req)
	}
	retry := RetryOptions{Timeout: 50 * time.Millisecond, MinAttemptWindow: 10 * time.Millisecond, InitialBackoff: time.Millisecond}

	result, err := newTestClient(t, newSequence(slow), retry).Enqueue(context.Background(), "q", []EnqueueItem{{Item: 1}}, nil)

	if err != nil || !result.Success {
		t.Fatalf("result=%+v err=%v", result, err)
	}
}

func TestNotDeliveredUntilTimeRunsOutReturnsUnavailable(t *testing.T) {
	transport := newSequence(notDelivered)
	retry := RetryOptions{Timeout: 200 * time.Millisecond, MinAttemptWindow: 10 * time.Millisecond, InitialBackoff: time.Millisecond, MaxBackoff: 5 * time.Millisecond}

	err := newTestClient(t, transport, retry).Acknowledge(context.Background(), "q", "L1", nil)

	e := requireCode(t, err, CodeUnavailable)
	if transport.count() <= 1 || e.Operation != "Acknowledge" || e.QueueID != "q" {
		t.Fatalf("attempts=%d err=%+v", transport.count(), e)
	}
}

func TestNoAttemptStartsWithLessThanTheMinimumWindowLeft(t *testing.T) {
	transport := newSequence(notDelivered)
	retry := RetryOptions{Timeout: time.Second, MinAttemptWindow: 5 * time.Second, InitialBackoff: time.Millisecond, MaxBackoff: 5 * time.Millisecond}

	err := newTestClient(t, transport, retry).Acknowledge(context.Background(), "q", "L1", nil)

	requireCode(t, err, CodeUnavailable)
	if transport.count() != 1 {
		t.Fatalf("want 1 attempt, got %d", transport.count())
	}
}

func TestConnectionRefusedIsNotDeliveredAndRetried(t *testing.T) {
	refused := func(req *http.Request) (*http.Response, error) {
		return nil, &net.OpError{Op: "dial", Net: "tcp", Err: syscall.ECONNREFUSED}
	}
	transport := newSequence(refused, refused, okEnqueue)

	if _, err := newTestClient(t, transport, fastRetry).Enqueue(context.Background(), "q", []EnqueueItem{{Item: 1}}, nil); err != nil {
		t.Fatal(err)
	}

	if transport.count() != 3 {
		t.Fatalf("want 3 attempts, got %d", transport.count())
	}
}

func TestUnknownIsNotRetriedForADequeue(t *testing.T) {
	transport := newSequence(unknown)

	_, err := newTestClient(t, transport, fastRetry).DequeueLocked(context.Background(), "q", nil)

	e := requireCode(t, err, CodeDeliveryUnknown)
	if transport.count() != 1 || e.Operation != "DequeueLocked" {
		t.Fatalf("attempts=%d operation=%q", transport.count(), e.Operation)
	}
}

func TestABrokenConnectionAfterSendingIsUnknown(t *testing.T) {
	reset := func(req *http.Request) (*http.Response, error) {
		return nil, &net.OpError{Op: "read", Net: "tcp", Err: syscall.ECONNRESET}
	}
	transport := newSequence(reset)

	err := newTestClient(t, transport, fastRetry).Acknowledge(context.Background(), "q", "L1", nil)

	requireCode(t, err, CodeDeliveryUnknown)
	if transport.count() != 1 {
		t.Fatalf("want 1 attempt, got %d", transport.count())
	}
}

func TestUnknownIsNotRetriedForAnEnqueueWithUnkeyedItemsAndReportsTheKeys(t *testing.T) {
	transport := newSequence(unknown)

	_, err := newTestClient(t, transport, fastRetry).Enqueue(context.Background(), "q",
		[]EnqueueItem{{Item: 1, IdempotencyKey: "k1"}, {Item: 2}}, nil)

	e := requireCode(t, err, CodeDeliveryUnknown)
	if transport.count() != 1 {
		t.Fatalf("want 1 attempt, got %d", transport.count())
	}
	if len(e.IdempotencyKeys) != 2 || e.IdempotencyKeys[0] != "k1" || e.IdempotencyKeys[1] != "" {
		t.Fatalf("keys = %q", e.IdempotencyKeys)
	}
}

func TestUnknownIsRetriedForAFullyKeyedEnqueue(t *testing.T) {
	transport := newSequence(unknown, okEnqueue)

	if _, err := newTestClient(t, transport, fastRetry).Enqueue(context.Background(), "q", []EnqueueItem{{Item: 1, IdempotencyKey: "k1"}}, nil); err != nil {
		t.Fatal(err)
	}

	if transport.count() != 2 {
		t.Fatalf("want 2 attempts, got %d", transport.count())
	}
}

func TestAutoIdempotencyKeysFillMissingKeysKeepGivenOnesAndMakeUnknownRetryable(t *testing.T) {
	transport := newSequence(unknown, okEnqueue)
	retry := fastRetry
	retry.AutoIdempotencyKeys = true

	if _, err := newTestClient(t, transport, retry).Enqueue(context.Background(), "q",
		[]EnqueueItem{{Item: 1, IdempotencyKey: "mine"}, {Item: 2}}, nil); err != nil {
		t.Fatal(err)
	}

	if len(transport.bodies) != 2 || transport.bodies[0] != transport.bodies[1] {
		t.Fatalf("the retry must re-send the same generated key: %q", transport.bodies)
	}
	var body struct {
		Items []struct {
			IdempotencyKey string `json:"idempotencyKey"`
		} `json:"items"`
	}
	if err := json.Unmarshal([]byte(transport.bodies[0]), &body); err != nil {
		t.Fatal(err)
	}
	if body.Items[0].IdempotencyKey != "mine" || !regexp.MustCompile(`^[0-9a-f]{32}$`).MatchString(body.Items[1].IdempotencyKey) {
		t.Fatalf("keys = %+v", body.Items)
	}
}

func TestA503WithoutTheMarkerIsNotADeliveryFailure(t *testing.T) {
	transport := newSequence(respond(503, map[string]any{"message": "proxy says no"}, nil))

	err := newTestClient(t, transport, fastRetry).Acknowledge(context.Background(), "q", "L1", nil)

	var e *Error
	if !errors.As(err, &e) || e.Code == CodeUnavailable || e.Code == CodeDeliveryUnknown {
		t.Fatalf("want a plain DaprMQ error, got %v", err)
	}
	if transport.count() != 1 {
		t.Fatalf("want 1 attempt, got %d", transport.count())
	}
}

func TestRetriesOffSendNoRetryTimeoutAndMakeOneAttempt(t *testing.T) {
	transport := newSequence(notDelivered)

	err := newTestClient(t, transport, RetryOptions{Timeout: NoRetries}).Acknowledge(context.Background(), "q", "L1", nil)

	requireCode(t, err, CodeUnavailable)
	if transport.count() != 1 || transport.requests[0].Header.Get("daprmq-retry-timeout") != "" {
		t.Fatalf("attempts=%d header=%q", transport.count(), transport.requests[0].Header.Get("daprmq-retry-timeout"))
	}
}

func TestCallerCancellationStopsRetryingAsCancellation(t *testing.T) {
	transport := newSequence(notDelivered)
	retry := RetryOptions{Timeout: 30 * time.Second, MinAttemptWindow: 10 * time.Millisecond, InitialBackoff: 20 * time.Millisecond, MaxBackoff: 20 * time.Millisecond}
	ctx, cancel := context.WithTimeout(context.Background(), 100*time.Millisecond)
	defer cancel()

	started := time.Now()
	err := newTestClient(t, transport, retry).Acknowledge(ctx, "q", "L1", nil)

	if !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("want context.DeadlineExceeded, got %v", err)
	}
	var e *Error
	if errors.As(err, &e) {
		t.Fatalf("cancellation must not surface as a DaprMQ error: %v", err)
	}
	if time.Since(started) > 2*time.Second {
		t.Fatal("took too long to stop")
	}
}

func TestCallerCancellationDuringAnAttemptIsCancellation(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	hang := func(req *http.Request) (*http.Response, error) {
		cancel()
		<-req.Context().Done()
		return nil, req.Context().Err()
	}

	err := newTestClient(t, newSequence(hang), fastRetry).Acknowledge(ctx, "q", "L1", nil)

	if !errors.Is(err, context.Canceled) {
		t.Fatalf("want context.Canceled, got %v", err)
	}
}

func TestDefaultHTTPClientHasThePerCallLimit(t *testing.T) {
	c, err := NewClient("http://localhost:5000", "localhost:5001", nil)
	if err != nil {
		t.Fatal(err)
	}
	defer c.Close()

	if c.http.Timeout != 100*time.Second {
		t.Fatalf("timeout = %s", c.http.Timeout)
	}
}
