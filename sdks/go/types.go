package daprmq

import (
	"encoding/json"
	"math"
	"net/http"
	"time"

	"google.golang.org/grpc"
)

// Priority lanes. Lower numbers are dequeued first; 2 and above are lower-priority lanes.
const (
	PriorityFastLane = 0
	PriorityNormal   = 1
)

// Ptr returns a pointer to v, for optional fields such as [EnqueueItem.Priority].
func Ptr[T any](v T) *T { return &v }

// ClientOptions configures [NewClient]. A nil *ClientOptions uses the defaults.
type ClientOptions struct {
	// HTTPClient sends the REST calls. Default: one with a 100 s per-attempt limit (the
	// server's own per-call limit), so neither side gives up on a call the other is still running.
	HTTPClient *http.Client
	// GRPCDialOptions are applied after the default plaintext credentials, so pass
	// grpc.WithTransportCredentials to use TLS.
	GRPCDialOptions []grpc.DialOption
	Retry           RetryOptions
}

// NoRetries, as [RetryOptions.Timeout], turns client retries off: each call makes one attempt.
// (A zero Timeout can't mean "off" in Go, since it is also what an unset field holds.)
const NoRetries time.Duration = -1

// RetryOptions controls how calls ride out a DaprMQ that can't serve them yet
// (sdks/testing/RETRIES_AND_READINESS.md). The zero value is the default behaviour.
type RetryOptions struct {
	// Timeout is how long one call may keep retrying a DaprMQ that can't serve it; it is also sent
	// to the server as its retry window. It never cuts short a call that was delivered.
	// 0 means the default, 30 s. [NoRetries] turns client retries off.
	Timeout time.Duration
	// AutoIdempotencyKeys gives each enqueued item without an IdempotencyKey a fresh random one,
	// so an enqueue whose outcome is unknown is retried safely. Costs the server one extra state
	// write per item.
	AutoIdempotencyKeys bool

	// Tuning, normally left alone (0 = default). No attempt starts with less than
	// MinAttemptWindow (6 s) of Timeout left, since the server takes about 5 s to report it can't
	// serve. Backoff starts at InitialBackoff (100 ms) and doubles up to MaxBackoff (2 s), with
	// full jitter.
	MinAttemptWindow time.Duration
	InitialBackoff   time.Duration
	MaxBackoff       time.Duration
}

// EnqueueItem is one item to enqueue.
type EnqueueItem struct {
	// Item is marshalled to JSON.
	Item any
	// Priority defaults to PriorityNormal (1). Use Ptr(PriorityFastLane) for the fast lane.
	Priority *int
	// IdempotencyKey, when set and the queue has de-duplication on, makes a repeat of this item
	// within the server's window a no-op (counted in ItemsDeduplicated).
	IdempotencyKey string
	// SessionID routes the item to that session of the queue.
	SessionID string
}

// EnqueueOptions is reserved for future options; pass nil.
type EnqueueOptions struct{}

type EnqueueResult struct {
	Success           bool
	Message           string
	ItemsEnqueued     int
	ItemsDeduplicated int
}

// DequeueLockedOptions configures [Client.DequeueLocked]. A nil value uses the defaults.
type DequeueLockedOptions struct {
	// Count is the most items to return (1-1000). Default 1.
	Count int
	// TTL is how long the locks last, in whole seconds (rounded up). Default 30 s.
	TTL time.Duration
	// LeaseID is required on a leased session queue.
	LeaseID string
	// AllowCompetingConsumers lets each caller hold its own locks. By default a queue serves one
	// lock at a time and further locked dequeues come back Locked.
	AllowCompetingConsumers bool
}

type DequeueLockedItem struct {
	Item          json.RawMessage
	Priority      int
	LockID        string
	LockExpiresAt time.Time
}

// DequeueLockedResult holds the dequeued items. An empty queue has no items and Locked false;
// a queue whose lock is held by another consumer has Locked true.
type DequeueLockedResult struct {
	Items   []DequeueLockedItem
	Locked  bool
	Message string
}

// LockOptions configures calls that act on a lock. A nil value uses the defaults.
type LockOptions struct {
	// LeaseID is required on a leased session queue.
	LeaseID string
}

// AcknowledgeOutcome is the outcome of one lock in [Client.AcknowledgeBatch].
type AcknowledgeOutcome string

const (
	// AcknowledgeOutcomeAcknowledged: settled by this call.
	AcknowledgeOutcomeAcknowledged AcknowledgeOutcome = "ACKNOWLEDGED"
	// AcknowledgeOutcomeLockNotFound: no such lock - never existed, or already settled. After the
	// client retried a batch whose outcome was unknown, this can mean the earlier attempt settled it.
	AcknowledgeOutcomeLockNotFound AcknowledgeOutcome = "LOCK_NOT_FOUND"
	// AcknowledgeOutcomeLockExpired: plain queue only; the lock's TTL passed, so the item is
	// returning to the queue.
	AcknowledgeOutcomeLockExpired AcknowledgeOutcome = "LOCK_EXPIRED"
	// AcknowledgeOutcomeInvalidLockID: empty lock id.
	AcknowledgeOutcomeInvalidLockID AcknowledgeOutcome = "INVALID_LOCK_ID"
)

type LockAcknowledgeResult struct {
	LockID  string
	Outcome AcknowledgeOutcome
}

type AcknowledgeBatchResult struct {
	ItemsAcknowledged int
	// Results has one entry per requested lock, in request order.
	Results []LockAcknowledgeResult
}

type NackResult struct {
	// DeadLettered is true if the nack exceeded the server's max delivery count, so the item was
	// dead-lettered instead.
	DeadLettered  bool
	DeliveryCount int
	DLQID         string
}

// AcceptSessionOptions configures [Client.AcceptSession]. A nil value uses the defaults.
type AcceptSessionOptions struct {
	// SessionID claims that session. Empty claims any available one.
	SessionID string
	// LeaseDuration in whole seconds (1-300, rounded up). Default 30 s.
	LeaseDuration time.Duration
}

// RenewSessionLeaseOptions configures [Client.RenewSessionLease]. A nil value uses the defaults.
type RenewSessionLeaseOptions struct {
	// Additional lease time in whole seconds (rounded up). Default 30 s.
	Additional time.Duration
}

type SessionLease struct {
	SessionID      string
	LeaseID        string
	LeaseExpiresAt time.Time
}

// ConsumeSessionOptions configures [Client.ConsumeSession]. A nil value uses the defaults.
type ConsumeSessionOptions struct {
	// SessionID claims that session. Empty claims any available one.
	SessionID string
	// LeaseDuration in whole seconds (1-300, rounded up). The server renews it for as long as the
	// stream stays open. Default 30 s.
	LeaseDuration time.Duration
	// PrefetchCount is how many delivered but unsettled items to keep in flight. Default 1. Above
	// 1, a Nack can reorder the session: items already in flight are delivered before it comes back.
	PrefetchCount int
	// SessionIdleTimeout ends the stream (io.EOF) once no message has arrived for this long,
	// releasing the session. Default: the lease duration.
	SessionIdleTimeout time.Duration
}

// WaitForReadyOptions configures [Client.WaitForReady]. A nil value uses the defaults.
type WaitForReadyOptions struct {
	// Service is the gRPC health service to watch. Default [OperationsHealthService]; use
	// "daprmq.DaprMQ" to wait only for this server instance.
	Service string
}

// wholeSeconds rounds d up to whole seconds, or returns def when d is zero or less.
func wholeSeconds(d time.Duration, def int) int {
	if d <= 0 {
		return def
	}
	return int((d + time.Second - 1) / time.Second)
}

func unixSeconds(f float64) time.Time {
	sec := math.Floor(f)
	return time.Unix(int64(sec), int64(math.Round((f-sec)*1e9)))
}
