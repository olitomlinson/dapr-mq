package daprmq

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/olitomlinson/dapr-mq/sdks/go/internal/pb"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials/insecure"
	healthpb "google.golang.org/grpc/health/grpc_health_v1"
	"google.golang.org/grpc/status"
)

// OperationsHealthService is the health service [Client.WaitForReady] watches by default:
// SERVING means queue operations can be served end to end.
const OperationsHealthService = "daprmq.DaprMQ.operations"

// Client is a DaprMQ client. Every operation is a REST call except [Client.ConsumeSession] and
// [Client.WaitForReady], which use gRPC. A Client is safe for concurrent use; share one per
// process, since its single gRPC connection multiplexes any number of session streams.
type Client struct {
	baseURL    string
	http       *http.Client
	ownsHTTP   bool
	retry      RetryOptions
	conn       *grpc.ClientConn
	grpc       pb.DaprMQClient
	health     healthpb.HealthClient
	closeOnce  sync.Once
	closeError error
}

// NewClient creates a client for the DaprMQ REST API at httpBaseURL (e.g. "http://localhost:8002")
// and gRPC API at grpcAddress (e.g. "localhost:8003"). It doesn't connect until the first call.
func NewClient(httpBaseURL, grpcAddress string, options *ClientOptions) (*Client, error) {
	if httpBaseURL == "" {
		return nil, errors.New("daprmq: httpBaseURL is required")
	}
	if grpcAddress == "" {
		return nil, errors.New("daprmq: grpcAddress is required")
	}
	if options == nil {
		options = &ClientOptions{}
	}

	c := &Client{baseURL: strings.TrimRight(httpBaseURL, "/"), http: options.HTTPClient, retry: options.Retry.withDefaults()}
	if c.http == nil {
		c.http = &http.Client{Timeout: 100 * time.Second}
		c.ownsHTTP = true
	}

	dialOptions := append([]grpc.DialOption{grpc.WithTransportCredentials(insecure.NewCredentials())}, options.GRPCDialOptions...)
	conn, err := grpc.NewClient(grpcAddress, dialOptions...)
	if err != nil {
		return nil, fmt.Errorf("daprmq: %w", err)
	}
	c.conn, c.grpc, c.health = conn, pb.NewDaprMQClient(conn), healthpb.NewHealthClient(conn)
	return c, nil
}

// Close releases the client's gRPC connection, ending any open session streams. Calling it more
// than once is harmless.
func (c *Client) Close() error {
	c.closeOnce.Do(func() {
		if c.ownsHTTP {
			c.http.CloseIdleConnections()
		}
		if c.conn != nil {
			c.closeError = c.conn.Close()
		}
	})
	return c.closeError
}

type enqueueWireItem struct {
	Item           any    `json:"item"`
	Priority       int    `json:"priority"`
	IdempotencyKey string `json:"idempotencyKey,omitempty"`
	SessionID      string `json:"sessionId,omitempty"`
}

// Enqueue adds items to the back of their priority lane, in order (1-10000 items per call).
func (c *Client) Enqueue(ctx context.Context, queueID string, items []EnqueueItem, _ *EnqueueOptions) (EnqueueResult, error) {
	// Keys are fixed before the first attempt, so a retry re-sends the same ones.
	keys := make([]string, len(items))
	allKeyed := true
	wire := make([]enqueueWireItem, len(items))
	for i, item := range items {
		keys[i] = item.IdempotencyKey
		if keys[i] == "" && c.retry.AutoIdempotencyKeys {
			keys[i] = newKey()
		}
		allKeyed = allKeyed && keys[i] != ""
		priority := PriorityNormal
		if item.Priority != nil {
			priority = *item.Priority
		}
		wire[i] = enqueueWireItem{Item: item.Item, Priority: priority, IdempotencyKey: keys[i], SessionID: item.SessionID}
	}

	// An unknown outcome is only safe to repeat when the server can de-duplicate every item.
	resp, err := c.send(ctx, request{
		operation: "Enqueue", queueID: queueID, path: queuePath(queueID, "enqueue"),
		body: map[string]any{"items": wire}, unknownIsRetryable: allKeyed, idempotencyKeys: keys,
	})
	if err != nil {
		return EnqueueResult{}, err
	}
	if !resp.ok() {
		return EnqueueResult{}, genericError(resp)
	}

	var result EnqueueResult
	return result, decode(resp, &result)
}

// DequeueLocked takes items off the front of the queue under a lock. Settle each with
// Acknowledge, AcknowledgeBatch, Nack or DeadLetter before the lock expires, or ExtendLock it;
// an expired lock returns its item to the position it was taken from.
func (c *Client) DequeueLocked(ctx context.Context, queueID string, options *DequeueLockedOptions) (DequeueLockedResult, error) {
	if options == nil {
		options = &DequeueLockedOptions{}
	}
	headers := map[string]string{
		"require-ack": "true",
		"count":       strconv.Itoa(max(options.Count, 1)),
		"ttl-seconds": strconv.Itoa(wholeSeconds(options.TTL, 30)),
	}
	if options.LeaseID != "" {
		headers["lease-id"] = options.LeaseID
	}
	if options.AllowCompetingConsumers {
		headers["allow-competing-consumers"] = "true"
	}

	resp, err := c.send(ctx, request{operation: "DequeueLocked", queueID: queueID, path: queuePath(queueID, "dequeue"), headers: headers})
	if err != nil {
		return DequeueLockedResult{}, err
	}
	switch {
	case resp.status == http.StatusNoContent:
		return DequeueLockedResult{}, nil
	case resp.status == http.StatusLocked:
		return DequeueLockedResult{Locked: true, Message: parseErrorBody(resp.body).Message}, nil
	case resp.status == http.StatusGone:
		// Dequeue's guard rejection carries no wire error code: 410 is the session-lease guard.
		return DequeueLockedResult{}, &Error{Code: CodeSessionLeaseExpired, Message: errorMessage(resp.status, resp.body), StatusCode: resp.status}
	case !resp.ok():
		// 400 covers both a bad or missing lease id and ordinary validation (e.g. a bad count).
		return DequeueLockedResult{}, &Error{Code: CodeValidation, Message: errorMessage(resp.status, resp.body), StatusCode: resp.status}
	}

	var wire struct {
		Items []struct {
			Item          json.RawMessage `json:"item"`
			Priority      int             `json:"priority"`
			LockID        string          `json:"lockId"`
			LockExpiresAt float64         `json:"lockExpiresAt"`
		} `json:"items"`
		Locked  bool   `json:"locked"`
		Message string `json:"message"`
	}
	if err := decode(resp, &wire); err != nil {
		return DequeueLockedResult{}, err
	}
	result := DequeueLockedResult{Locked: wire.Locked, Message: wire.Message, Items: make([]DequeueLockedItem, len(wire.Items))}
	for i, item := range wire.Items {
		result.Items[i] = DequeueLockedItem{Item: item.Item, Priority: item.Priority, LockID: item.LockID, LockExpiresAt: unixSeconds(item.LockExpiresAt)}
	}
	return result, nil
}

// Acknowledge permanently removes a locked item.
func (c *Client) Acknowledge(ctx context.Context, queueID, lockID string, options *LockOptions) error {
	resp, err := c.send(ctx, lockRequest("Acknowledge", queueID, "acknowledge", map[string]any{"lockId": lockID}, options))
	if err != nil {
		return err
	}
	return lockFailure(resp)
}

// AcknowledgeBatch acknowledges up to 1,000 locks in one call, with an outcome per lock. One lock
// that expired or was already settled doesn't fail the rest; only a whole-call failure (bad lease,
// invalid request) is an error. An unknown outcome is retried automatically, since re-sending is
// harmless; after such a retry, AcknowledgeOutcomeLockNotFound can mean "already settled".
func (c *Client) AcknowledgeBatch(ctx context.Context, queueID string, lockIDs []string, options *LockOptions) (AcknowledgeBatchResult, error) {
	r := lockRequest("AcknowledgeBatch", queueID, "acknowledge-batch", map[string]any{"lockIds": lockIDs}, options)
	r.unknownIsRetryable = true
	resp, err := c.send(ctx, r)
	if err != nil {
		return AcknowledgeBatchResult{}, err
	}
	if err := lockFailure(resp); err != nil {
		return AcknowledgeBatchResult{}, err
	}

	var wire struct {
		ItemsAcknowledged int `json:"itemsAcknowledged"`
		Results           []struct {
			LockID  string `json:"lockId"`
			Outcome string `json:"outcome"`
		} `json:"results"`
	}
	if err := decode(resp, &wire); err != nil {
		return AcknowledgeBatchResult{}, err
	}
	result := AcknowledgeBatchResult{ItemsAcknowledged: wire.ItemsAcknowledged, Results: make([]LockAcknowledgeResult, len(wire.Results))}
	for i, r := range wire.Results {
		result.Results[i] = LockAcknowledgeResult{LockID: r.LockID, Outcome: AcknowledgeOutcome(r.Outcome)}
	}
	return result, nil
}

// ExtendLock adds additional (whole seconds, rounded up) to a lock's remaining time.
func (c *Client) ExtendLock(ctx context.Context, queueID, lockID string, additional time.Duration, options *LockOptions) error {
	body := struct {
		LockID               string `json:"lockId"`
		AdditionalTTLSeconds int    `json:"additionalTtlSeconds"`
	}{lockID, wholeSeconds(additional, 30)}
	resp, err := c.send(ctx, lockRequest("ExtendLock", queueID, "extend-lock", body, options))
	if err != nil || resp.ok() {
		return err
	}

	// ExtendLock's error body carries no wire error code, so map by status.
	code := CodeValidation
	switch resp.status {
	case http.StatusGone:
		code = CodeLockExpired
	case http.StatusNotFound:
		code = CodeLockNotFound
	}
	return &Error{Code: code, Message: errorMessage(resp.status, resp.body), StatusCode: resp.status}
}

// DeadLetter moves a locked item to the queue's dead letter queue, "{queueID}-deadletter".
func (c *Client) DeadLetter(ctx context.Context, queueID, lockID string, options *LockOptions) error {
	resp, err := c.send(ctx, lockRequest("DeadLetter", queueID, "deadletter", map[string]any{"lockId": lockID}, options))
	if err != nil {
		return err
	}
	return lockFailure(resp)
}

// Nack returns a locked item to its original position in the queue. It counts as a delivery
// attempt: past the server's max delivery count the item is dead-lettered instead.
func (c *Client) Nack(ctx context.Context, queueID, lockID string, options *LockOptions) (NackResult, error) {
	resp, err := c.send(ctx, lockRequest("Nack", queueID, "nack", map[string]any{"lockId": lockID}, options))
	if err != nil {
		return NackResult{}, err
	}
	if err := lockFailure(resp); err != nil {
		return NackResult{}, err
	}

	var wire struct {
		DeadLettered  bool   `json:"deadLettered"`
		DeliveryCount int    `json:"deliveryCount"`
		DLQID         string `json:"dlqId"`
	}
	if err := decode(resp, &wire); err != nil {
		return NackResult{}, err
	}
	return NackResult(wire), nil
}

// AcceptSession claims a session lease: options.SessionID, or any available session. It returns
// nil and no error when no session is available. Once claimed, dequeue from and settle against
// "{queueID}-session-{sessionID}" with the lease's LeaseID.
func (c *Client) AcceptSession(ctx context.Context, queueID string, options *AcceptSessionOptions) (*SessionLease, error) {
	if options == nil {
		options = &AcceptSessionOptions{}
	}
	body := struct {
		SessionID    *string `json:"sessionId"`
		LeaseSeconds int     `json:"leaseSeconds"`
	}{LeaseSeconds: wholeSeconds(options.LeaseDuration, 30)}
	if options.SessionID != "" {
		body.SessionID = &options.SessionID
	}

	resp, err := c.send(ctx, request{operation: "AcceptSession", queueID: queueID, path: queuePath(queueID, "sessions/accept"), body: body})
	if err != nil {
		return nil, err
	}
	if resp.status == http.StatusNoContent {
		return nil, nil
	}
	if !resp.ok() {
		code := CodeValidation
		switch resp.status {
		case http.StatusNotFound:
			code = CodeSessionNotFound
		case http.StatusLocked:
			code = CodeSessionLocked
		case http.StatusBadGateway:
			code = CodeSessionActorUnavailable
		}
		return nil, &Error{Code: code, Message: errorMessage(resp.status, resp.body), StatusCode: resp.status}
	}

	var wire struct {
		SessionID      string  `json:"sessionId"`
		LeaseID        string  `json:"leaseId"`
		LeaseExpiresAt float64 `json:"leaseExpiresAt"`
	}
	if err := decode(resp, &wire); err != nil {
		return nil, err
	}
	return &SessionLease{SessionID: wire.SessionID, LeaseID: wire.LeaseID, LeaseExpiresAt: unixSeconds(wire.LeaseExpiresAt)}, nil
}

// RenewSessionLease extends a claimed session's lease.
func (c *Client) RenewSessionLease(ctx context.Context, queueID, sessionID, leaseID string, options *RenewSessionLeaseOptions) (SessionLease, error) {
	if options == nil {
		options = &RenewSessionLeaseOptions{}
	}
	body := struct {
		LeaseID           string `json:"leaseId"`
		AdditionalSeconds int    `json:"additionalSeconds"`
	}{leaseID, wholeSeconds(options.Additional, 30)}

	resp, err := c.send(ctx, request{
		operation: "RenewSessionLease", queueID: queueID,
		path: queuePath(queueID, "sessions/"+url.PathEscape(sessionID)+"/renew"), body: body,
	})
	if err != nil {
		return SessionLease{}, err
	}
	if !resp.ok() {
		code := CodeInvalidLeaseID
		if resp.status == http.StatusGone {
			code = CodeSessionLeaseExpired
		}
		return SessionLease{}, &Error{Code: code, Message: errorMessage(resp.status, resp.body), StatusCode: resp.status}
	}

	var wire struct {
		NewExpiresAt float64 `json:"newExpiresAt"`
	}
	if err := decode(resp, &wire); err != nil {
		return SessionLease{}, err
	}
	return SessionLease{SessionID: sessionID, LeaseID: leaseID, LeaseExpiresAt: unixSeconds(wire.NewExpiresAt)}, nil
}

// ReleaseSession gives up a session lease so another consumer can claim it straight away.
// Releasing an already-released session succeeds.
func (c *Client) ReleaseSession(ctx context.Context, queueID, sessionID, leaseID string) error {
	resp, err := c.send(ctx, request{
		operation: "ReleaseSession", queueID: queueID,
		path: queuePath(queueID, "sessions/"+url.PathEscape(sessionID)+"/release"), body: map[string]any{"leaseId": leaseID},
	})
	if err != nil || resp.ok() {
		return err
	}
	return &Error{Code: CodeInvalidLeaseID, Message: errorMessage(resp.status, resp.body), StatusCode: resp.status}
}

// WaitForReady blocks until the server reports SERVING for the health service (by default
// [OperationsHealthService]: queue operations can be served). It reconnects while the server
// isn't listening and is bounded only by ctx. It returns an error wrapping
// [errors.ErrUnsupported] if the server doesn't expose the gRPC health service.
func (c *Client) WaitForReady(ctx context.Context, options *WaitForReadyOptions) error {
	service := OperationsHealthService
	if options != nil && options.Service != "" {
		service = options.Service
	}

	backoff := 250 * time.Millisecond
	for {
		serving, err := c.watchHealth(ctx, service, &backoff)
		if serving {
			return nil
		}
		if ctx.Err() != nil {
			return ctx.Err()
		}
		switch status.Code(err) {
		case codes.OK, codes.Unavailable:
			// Stream ended before SERVING, or the server isn't listening yet: reconnect.
		case codes.DeadlineExceeded:
			// The server enforced ctx's propagated deadline a moment before ctx itself expired.
			if _, ok := ctx.Deadline(); ok {
				<-ctx.Done()
				return ctx.Err()
			}
			return err
		case codes.Unimplemented:
			return fmt.Errorf("daprmq: the server does not expose the gRPC health service; upgrade the server: %w", errors.ErrUnsupported)
		default:
			return err
		}
		if err := sleep(ctx, backoff); err != nil {
			return err
		}
		backoff = min(backoff*2, 2*time.Second)
	}
}

func (c *Client) watchHealth(ctx context.Context, service string, backoff *time.Duration) (bool, error) {
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	stream, err := c.health.Watch(ctx, &healthpb.HealthCheckRequest{Service: service})
	if err != nil {
		return false, err
	}
	for {
		resp, err := stream.Recv()
		if errors.Is(err, io.EOF) {
			return false, nil
		}
		if err != nil {
			return false, err
		}
		if resp.Status == healthpb.HealthCheckResponse_SERVING {
			return true, nil
		}
		*backoff = 250 * time.Millisecond
	}
}

func queuePath(queueID, suffix string) string {
	return "/queue/" + url.PathEscape(queueID) + "/" + suffix
}

func lockRequest(operation, queueID, suffix string, body any, options *LockOptions) request {
	r := request{operation: operation, queueID: queueID, path: queuePath(queueID, suffix), body: body}
	if options != nil && options.LeaseID != "" {
		r.headers = map[string]string{"lease-id": options.LeaseID}
	}
	return r
}

// lockFailure maps a failed lock call (its body carries the wire error code) to an error.
func lockFailure(resp *response) error {
	if resp.ok() {
		return nil
	}
	b := parseErrorBody(resp.body)
	return lockError(b.ErrorCode, errorMessage(resp.status, resp.body), resp.status)
}

func genericError(resp *response) error {
	code := Code("")
	switch resp.status {
	case http.StatusBadRequest:
		code = CodeValidation
	case http.StatusNotFound:
		code = CodeNotFound
	}
	return &Error{Code: code, Message: errorMessage(resp.status, resp.body), StatusCode: resp.status}
}

func decode(resp *response, v any) error {
	if err := json.Unmarshal(resp.body, v); err != nil {
		return &Error{Message: fmt.Sprintf("malformed response body: %v", err), StatusCode: resp.status, cause: err}
	}
	return nil
}

func newKey() string {
	b := make([]byte, 16)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}
