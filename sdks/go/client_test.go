package daprmq

import (
	"context"
	"encoding/json"
	"testing"
	"time"
)

var bg = context.Background()

func TestEnqueuePostsItemsAndReturnsTheCounts(t *testing.T) {
	transport := newSequence(ok(map[string]any{"success": true, "message": "ok", "itemsEnqueued": 1, "itemsDeduplicated": 1}))

	result, err := newTestClient(t, transport, fastRetry).Enqueue(bg, "my queue", []EnqueueItem{
		{Item: map[string]string{"task": "a"}, Priority: Ptr(PriorityFastLane), IdempotencyKey: "k1", SessionID: "s1"},
		{Item: "b"},
	}, nil)
	if err != nil {
		t.Fatal(err)
	}

	req := transport.requests[0]
	if req.Method != "POST" || req.URL.EscapedPath() != "/queue/my%20queue/enqueue" {
		t.Fatalf("%s %s", req.Method, req.URL.EscapedPath())
	}
	var body struct {
		Items []map[string]any `json:"items"`
	}
	if err := json.Unmarshal([]byte(transport.bodies[0]), &body); err != nil {
		t.Fatal(err)
	}
	first, second := body.Items[0], body.Items[1]
	if first["priority"] != 0.0 || first["idempotencyKey"] != "k1" || first["sessionId"] != "s1" || first["item"].(map[string]any)["task"] != "a" {
		t.Fatalf("first = %v", first)
	}
	if second["priority"] != 1.0 || second["item"] != "b" {
		t.Fatalf("an unset priority is the normal lane (1): %v", second)
	}
	if _, has := second["idempotencyKey"]; has {
		t.Fatalf("an unkeyed item sends no key: %v", second)
	}
	if !result.Success || result.ItemsEnqueued != 1 || result.ItemsDeduplicated != 1 {
		t.Fatalf("result = %+v", result)
	}
}

func TestEnqueueSendsAGivenPriority(t *testing.T) {
	transport := newSequence(okEnqueue)

	if _, err := newTestClient(t, transport, fastRetry).Enqueue(bg, "q", []EnqueueItem{{Item: 1, Priority: Ptr(3)}}, nil); err != nil {
		t.Fatal(err)
	}

	if !contains(transport.bodies[0], `"priority":3`) {
		t.Fatal(transport.bodies[0])
	}
}

func TestEnqueueValidationErrorMapsToValidation(t *testing.T) {
	transport := newSequence(respond(400, map[string]any{"message": "bad priority"}, nil))

	_, err := newTestClient(t, transport, fastRetry).Enqueue(bg, "q", []EnqueueItem{{Item: 1}}, nil)

	e := requireCode(t, err, CodeValidation)
	if e.Message != "bad priority" {
		t.Fatalf("message = %q", e.Message)
	}
}

func TestDequeueLockedSendsOptionsAsHeadersAndReturnsItems(t *testing.T) {
	transport := newSequence(ok(map[string]any{
		"items":  []any{map[string]any{"item": map[string]int{"n": 1}, "priority": 1, "lockId": "L1", "lockExpiresAt": 1700000000.5}},
		"locked": false,
	}))

	result, err := newTestClient(t, transport, fastRetry).DequeueLocked(bg, "q", &DequeueLockedOptions{
		Count: 5, TTL: 60 * time.Second, LeaseID: "lease", AllowCompetingConsumers: true,
	})
	if err != nil {
		t.Fatal(err)
	}

	h := transport.requests[0].Header
	if h.Get("require-ack") != "true" || h.Get("count") != "5" || h.Get("ttl-seconds") != "60" ||
		h.Get("lease-id") != "lease" || h.Get("allow-competing-consumers") != "true" {
		t.Fatalf("headers = %v", h)
	}
	if transport.requests[0].URL.Path != "/queue/q/dequeue" {
		t.Fatal(transport.requests[0].URL.Path)
	}
	if len(result.Items) != 1 || result.Locked {
		t.Fatalf("result = %+v", result)
	}
	item := result.Items[0]
	if string(item.Item) != `{"n":1}` || item.LockID != "L1" || item.Priority != 1 ||
		!item.LockExpiresAt.Equal(time.Unix(1700000000, 500_000_000)) {
		t.Fatalf("item = %+v", item)
	}
}

func TestDequeueLockedDefaultsSendOneItemAndThirtySeconds(t *testing.T) {
	transport := newSequence(respond(204, nil, nil))

	if _, err := newTestClient(t, transport, fastRetry).DequeueLocked(bg, "q", nil); err != nil {
		t.Fatal(err)
	}

	h := transport.requests[0].Header
	if h.Get("count") != "1" || h.Get("ttl-seconds") != "30" || h.Get("lease-id") != "" || h.Get("allow-competing-consumers") != "" {
		t.Fatalf("headers = %v", h)
	}
}

func TestDequeueLockedOnAnEmptyQueueReturnsNoItemsAndNoError(t *testing.T) {
	result, err := newTestClient(t, newSequence(respond(204, nil, nil)), fastRetry).DequeueLocked(bg, "q", nil)

	if err != nil || len(result.Items) != 0 || result.Locked {
		t.Fatalf("result=%+v err=%v", result, err)
	}
}

func TestDequeueLockedOnALockedQueueReportsLocked(t *testing.T) {
	transport := newSequence(respond(423, map[string]any{"message": "held"}, nil))

	result, err := newTestClient(t, transport, fastRetry).DequeueLocked(bg, "q", nil)

	if err != nil || !result.Locked || result.Message != "held" || len(result.Items) != 0 {
		t.Fatalf("result=%+v err=%v", result, err)
	}
}

func TestDequeueLocked410IsSessionLeaseExpiredAnd400IsValidation(t *testing.T) {
	_, err := newTestClient(t, newSequence(respond(410, map[string]any{"message": "gone"}, nil)), fastRetry).DequeueLocked(bg, "q", nil)
	requireCode(t, err, CodeSessionLeaseExpired)

	_, err = newTestClient(t, newSequence(respond(400, nil, nil)), fastRetry).DequeueLocked(bg, "q", nil)
	requireCode(t, err, CodeValidation)
}

func TestAcknowledgeSendsTheLockAndLease(t *testing.T) {
	transport := newSequence(ok(map[string]any{"success": true}))

	if err := newTestClient(t, transport, fastRetry).Acknowledge(bg, "q", "L1", &LockOptions{LeaseID: "lease"}); err != nil {
		t.Fatal(err)
	}

	if transport.requests[0].URL.Path != "/queue/q/acknowledge" || transport.requests[0].Header.Get("lease-id") != "lease" ||
		transport.bodies[0] != `{"lockId":"L1"}` {
		t.Fatalf("%s %v %s", transport.requests[0].URL.Path, transport.requests[0].Header, transport.bodies[0])
	}
}

func TestLockErrorCodesMapToTheirCodes(t *testing.T) {
	cases := map[string]Code{
		"LOCK_NOT_FOUND":        CodeLockNotFound,
		"LOCK_EXPIRED":          CodeLockExpired,
		"SESSION_LEASE_EXPIRED": CodeSessionLeaseExpired,
		"INVALID_LEASE_ID":      CodeInvalidLeaseID,
		"INVALID_LOCK_ID":       CodeValidation,
		"INVALID_TTL":           CodeValidation,
		"VALIDATION_ERROR":      CodeValidation,
		"SOMETHING_ELSE":        Code("SOMETHING_ELSE"),
	}
	for wire, want := range cases {
		t.Run(wire, func(t *testing.T) {
			transport := newSequence(respond(404, map[string]any{"message": "m", "errorCode": wire}, nil))
			err := newTestClient(t, transport, fastRetry).Acknowledge(bg, "q", "L1", nil)
			requireCode(t, err, want)

			transport = newSequence(respond(404, map[string]any{"message": "m", "errorCode": wire}, nil))
			err = newTestClient(t, transport, fastRetry).DeadLetter(bg, "q", "L1", nil)
			requireCode(t, err, want)

			transport = newSequence(respond(404, map[string]any{"message": "m", "errorCode": wire}, nil))
			_, err = newTestClient(t, transport, fastRetry).Nack(bg, "q", "L1", nil)
			requireCode(t, err, want)
		})
	}
}

func TestAcknowledgeBatchReturnsOneOutcomePerLock(t *testing.T) {
	transport := newSequence(ok(map[string]any{
		"success": true, "itemsAcknowledged": 1,
		"results": []any{
			map[string]any{"lockId": "L1", "outcome": "ACKNOWLEDGED"},
			map[string]any{"lockId": "L2", "outcome": "LOCK_EXPIRED"},
		},
	}))

	result, err := newTestClient(t, transport, fastRetry).AcknowledgeBatch(bg, "q", []string{"L1", "L2"}, &LockOptions{LeaseID: "lease"})
	if err != nil {
		t.Fatal(err)
	}

	if transport.requests[0].URL.Path != "/queue/q/acknowledge-batch" || transport.bodies[0] != `{"lockIds":["L1","L2"]}` ||
		transport.requests[0].Header.Get("lease-id") != "lease" {
		t.Fatalf("%s %s", transport.requests[0].URL.Path, transport.bodies[0])
	}
	if result.ItemsAcknowledged != 1 || len(result.Results) != 2 ||
		result.Results[0] != (LockAcknowledgeResult{LockID: "L1", Outcome: AcknowledgeOutcomeAcknowledged}) ||
		result.Results[1].Outcome != AcknowledgeOutcomeLockExpired {
		t.Fatalf("result = %+v", result)
	}
}

func TestAcknowledgeBatchWholeCallFailureIsAnError(t *testing.T) {
	transport := newSequence(respond(400, map[string]any{"message": "too many", "errorCode": "VALIDATION_ERROR"}, nil))

	_, err := newTestClient(t, transport, fastRetry).AcknowledgeBatch(bg, "q", []string{"L1"}, nil)

	requireCode(t, err, CodeValidation)
}

func TestExtendLockSendsSecondsAndMapsStatuses(t *testing.T) {
	transport := newSequence(ok(map[string]any{"success": true}))

	if err := newTestClient(t, transport, fastRetry).ExtendLock(bg, "q", "L1", 45*time.Second, nil); err != nil {
		t.Fatal(err)
	}
	if transport.requests[0].URL.Path != "/queue/q/extend-lock" || transport.bodies[0] != `{"lockId":"L1","additionalTtlSeconds":45}` {
		t.Fatalf("%s %s", transport.requests[0].URL.Path, transport.bodies[0])
	}

	for status, want := range map[int]Code{410: CodeLockExpired, 404: CodeLockNotFound, 400: CodeValidation} {
		err := newTestClient(t, newSequence(respond(status, nil, nil)), fastRetry).ExtendLock(bg, "q", "L1", time.Second, nil)
		requireCode(t, err, want)
	}
}

func TestExtendLockRoundsAPartialSecondUp(t *testing.T) {
	transport := newSequence(ok(map[string]any{"success": true}))

	if err := newTestClient(t, transport, fastRetry).ExtendLock(bg, "q", "L1", 1500*time.Millisecond, nil); err != nil {
		t.Fatal(err)
	}

	if !contains(transport.bodies[0], `"additionalTtlSeconds":2`) {
		t.Fatal(transport.bodies[0])
	}
}

func TestDeadLetterPostsToTheDeadLetterRoute(t *testing.T) {
	transport := newSequence(ok(map[string]any{"status": "SUCCESS"}))

	if err := newTestClient(t, transport, fastRetry).DeadLetter(bg, "q", "L1", nil); err != nil {
		t.Fatal(err)
	}

	if transport.requests[0].URL.Path != "/queue/q/deadletter" {
		t.Fatal(transport.requests[0].URL.Path)
	}
}

func TestNackReturnsTheDeliveryOutcome(t *testing.T) {
	transport := newSequence(ok(map[string]any{"success": true, "deadLettered": true, "deliveryCount": 5, "dlqId": "q-deadletter"}))

	result, err := newTestClient(t, transport, fastRetry).Nack(bg, "q", "L1", nil)
	if err != nil {
		t.Fatal(err)
	}

	if transport.requests[0].URL.Path != "/queue/q/nack" || result != (NackResult{DeadLettered: true, DeliveryCount: 5, DLQID: "q-deadletter"}) {
		t.Fatalf("%s %+v", transport.requests[0].URL.Path, result)
	}
}

func TestAcceptSessionReturnsTheLease(t *testing.T) {
	transport := newSequence(ok(map[string]any{"sessionId": "s1", "leaseId": "lease", "leaseExpiresAt": 1700000000.0}))

	lease, err := newTestClient(t, transport, fastRetry).AcceptSession(bg, "q", &AcceptSessionOptions{SessionID: "s1", LeaseDuration: 10 * time.Second})
	if err != nil {
		t.Fatal(err)
	}

	if transport.requests[0].URL.Path != "/queue/q/sessions/accept" || transport.bodies[0] != `{"sessionId":"s1","leaseSeconds":10}` {
		t.Fatalf("%s %s", transport.requests[0].URL.Path, transport.bodies[0])
	}
	if lease.SessionID != "s1" || lease.LeaseID != "lease" || !lease.LeaseExpiresAt.Equal(time.Unix(1700000000, 0)) {
		t.Fatalf("lease = %+v", lease)
	}
}

func TestAcceptSessionAnyAvailableSendsNullSessionAndDefaultLease(t *testing.T) {
	transport := newSequence(respond(204, nil, nil))

	lease, err := newTestClient(t, transport, fastRetry).AcceptSession(bg, "q", nil)

	if err != nil || lease != nil {
		t.Fatalf("no session available is nil, nil: lease=%+v err=%v", lease, err)
	}
	if transport.bodies[0] != `{"sessionId":null,"leaseSeconds":30}` {
		t.Fatal(transport.bodies[0])
	}
}

func TestAcceptSessionErrorStatuses(t *testing.T) {
	for status, want := range map[int]Code{404: CodeSessionNotFound, 423: CodeSessionLocked, 502: CodeSessionActorUnavailable, 400: CodeValidation} {
		_, err := newTestClient(t, newSequence(respond(status, nil, nil)), fastRetry).AcceptSession(bg, "q", nil)
		requireCode(t, err, want)
	}
}

func TestRenewSessionLease(t *testing.T) {
	transport := newSequence(ok(map[string]any{"newExpiresAt": 1700000100.0}))

	lease, err := newTestClient(t, transport, fastRetry).RenewSessionLease(bg, "q", "s/1", "lease", nil)
	if err != nil {
		t.Fatal(err)
	}

	if transport.requests[0].URL.EscapedPath() != "/queue/q/sessions/s%2F1/renew" || transport.bodies[0] != `{"leaseId":"lease","additionalSeconds":30}` {
		t.Fatalf("%s %s", transport.requests[0].URL.EscapedPath(), transport.bodies[0])
	}
	if lease.SessionID != "s/1" || lease.LeaseID != "lease" || !lease.LeaseExpiresAt.Equal(time.Unix(1700000100, 0)) {
		t.Fatalf("lease = %+v", lease)
	}

	_, err = newTestClient(t, newSequence(respond(410, nil, nil)), fastRetry).RenewSessionLease(bg, "q", "s", "l", nil)
	requireCode(t, err, CodeSessionLeaseExpired)
	_, err = newTestClient(t, newSequence(respond(400, nil, nil)), fastRetry).RenewSessionLease(bg, "q", "s", "l", nil)
	requireCode(t, err, CodeInvalidLeaseID)
}

func TestReleaseSession(t *testing.T) {
	transport := newSequence(ok(map[string]any{"success": true}))

	if err := newTestClient(t, transport, fastRetry).ReleaseSession(bg, "q", "s1", "lease"); err != nil {
		t.Fatal(err)
	}

	if transport.requests[0].URL.Path != "/queue/q/sessions/s1/release" || transport.bodies[0] != `{"leaseId":"lease"}` {
		t.Fatalf("%s %s", transport.requests[0].URL.Path, transport.bodies[0])
	}

	err := newTestClient(t, newSequence(respond(400, nil, nil)), fastRetry).ReleaseSession(bg, "q", "s1", "wrong")
	requireCode(t, err, CodeInvalidLeaseID)
}

func TestCloseIsIdempotent(t *testing.T) {
	c := newTestClient(t, newSequence(okEnqueue), fastRetry)

	if err := c.Close(); err != nil {
		t.Fatal(err)
	}
	if err := c.Close(); err != nil {
		t.Fatal(err)
	}
}

func TestNewClientRequiresAnHTTPBaseURL(t *testing.T) {
	if _, err := NewClient("", "localhost:5001", nil); err == nil {
		t.Fatal("want an error")
	}
}

func TestPublishPostsItemsToTheTopicAndReturnsThePublishID(t *testing.T) {
	transport := newSequence(respond(202, map[string]any{"accepted": true, "publishId": "p1", "sequence": 7}, nil))

	result, err := newTestClient(t, transport, fastRetry).Publish(bg, "my topic", []EnqueueItem{
		{Item: map[string]string{"task": "a"}, Priority: Ptr(PriorityFastLane), IdempotencyKey: "k1"},
	}, nil)
	if err != nil {
		t.Fatal(err)
	}

	req := transport.requests[0]
	if req.Method != "POST" || req.URL.EscapedPath() != "/topic/my%20topic/publish" {
		t.Fatalf("%s %s", req.Method, req.URL.EscapedPath())
	}
	if !contains(transport.bodies[0], `"priority":0`) || !contains(transport.bodies[0], `"idempotencyKey":"k1"`) || !contains(transport.bodies[0], `"task":"a"`) {
		t.Fatal(transport.bodies[0])
	}
	if result.PublishID != "p1" || result.Sequence != 7 {
		t.Fatalf("result = %+v", result)
	}
}

func TestPublishValidationErrorMapsToValidation(t *testing.T) {
	transport := newSequence(respond(400, map[string]any{"message": "Items array cannot be empty"}, nil))

	_, err := newTestClient(t, transport, fastRetry).Publish(bg, "t", []EnqueueItem{{Item: 1}}, nil)

	requireCode(t, err, CodeValidation)
}

func TestSubscribePostsTheSubscriberAndReturnsItsQueue(t *testing.T) {
	transport := newSequence(respond(201, map[string]any{"success": true, "queueActorId": "t-sub-app"}, nil))

	result, err := newTestClient(t, transport, fastRetry).Subscribe(bg, "my topic", "app 1", &SubscribeOptions{DedupEnabled: Ptr(true)})
	if err != nil {
		t.Fatal(err)
	}

	req := transport.requests[0]
	if req.Method != "POST" || req.URL.EscapedPath() != "/topic/my%20topic/subscribers/app%201" {
		t.Fatalf("%s %s", req.Method, req.URL.EscapedPath())
	}
	if !contains(transport.bodies[0], `"dedupEnabled":true`) {
		t.Fatal(transport.bodies[0])
	}
	if result.QueueID != "t-sub-app" {
		t.Fatalf("result = %+v", result)
	}
}

func TestSubscribeAnExistingSubscriberIsSubscriberExists(t *testing.T) {
	transport := newSequence(respond(409, map[string]any{"message": "Subscriber already exists"}, nil))

	_, err := newTestClient(t, transport, fastRetry).Subscribe(bg, "t", "app", nil)

	requireCode(t, err, CodeSubscriberExists)
}

func TestTopicSubscriberQueueID(t *testing.T) {
	if got := TopicSubscriberQueueID("orders", "billing"); got != "orders-sub-billing" {
		t.Fatal(got)
	}
}
