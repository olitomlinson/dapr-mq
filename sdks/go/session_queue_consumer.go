package daprmq

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"sync"
	"time"
)

// SessionHandler handles one session message. Returning nil acks it; returning an error applies
// [SessionQueueConsumerOptions.OnHandlerError]. ctx is cancelled only when Stop gives up waiting
// for in-flight handlers (DrainTimeout).
type SessionHandler func(ctx context.Context, msg SessionMessage) error

// SessionMessage is one message handed to a [SessionHandler]. It carries no lease id: the server
// tracks the lease and applies it when it settles the message on the consumer's behalf.
type SessionMessage struct {
	QueueID   string
	SessionID string
	LockID    string
	Item      json.RawMessage
	Priority  int
}

// SessionHandlerFailureAction is what a [SessionQueueConsumer] does when its handler returns an error.
type SessionHandlerFailureAction int

const (
	// DeadLetterMessage dead-letters the message and carries on with the session (the default).
	DeadLetterMessage SessionHandlerFailureAction = iota
	// AbandonSession leaves the message unsettled and releases the session, so the message is
	// redelivered with the session; the slot then claims another session.
	AbandonSession
	// DeadLetterAndAbandonSession dead-letters the message, then abandons the session.
	DeadLetterAndAbandonSession
	// NackMessage returns the message to the front of the session for redelivery. It counts toward
	// the server's max delivery count, past which it is dead-lettered.
	NackMessage
)

// SessionQueueConsumerOptions configures a [SessionQueueConsumer]. Zero fields use the defaults.
type SessionQueueConsumerOptions struct {
	// MaxConcurrentSessions is how many sessions are consumed at once, each on its own stream.
	// Default 4.
	MaxConcurrentSessions int
	// TargetSessionID consumes only that session. Requires MaxConcurrentSessions == 1.
	TargetSessionID string
	// LeaseDuration, PrefetchCount and SessionIdleTimeout are passed to each stream; see
	// [ConsumeSessionOptions]. Session order holds only at the default PrefetchCount of 1 when
	// messages are nacked.
	LeaseDuration      time.Duration
	PrefetchCount      int
	SessionIdleTimeout time.Duration
	// MinBackoff and MaxBackoff bound the wait after a failed claim (no session available): it
	// doubles on each miss and resets once a claim succeeds. Defaults 1 s and 60 s.
	MinBackoff time.Duration
	MaxBackoff time.Duration
	// OnHandlerError defaults to DeadLetterMessage.
	OnHandlerError SessionHandlerFailureAction
	// DrainTimeout is how long Stop waits for in-flight handlers before cancelling them. Default 30 s.
	DrainTimeout time.Duration
}

// SessionQueueConsumer consumes a session-enabled queue with MaxConcurrentSessions independent
// slots. Each slot loops over a [Client.ConsumeSession] stream: claim a session, hand each message
// to the handler, settle it, and repeat until the session drains; then claim another. All slots
// share the one Client and its gRPC connection.
type SessionQueueConsumer struct {
	client  *Client
	queueID string
	handler SessionHandler
	options SessionQueueConsumerOptions

	// sleep is a test seam for the claim backoff.
	sleep func(ctx context.Context, d time.Duration) error

	mu         sync.Mutex
	started    bool
	stopCtx    context.Context // cancelled when stopping begins
	beginStop  context.CancelFunc
	handlerCtx context.Context // cancelled when draining gives up
	hardStop   context.CancelFunc
	slots      sync.WaitGroup
	stopOnce   sync.Once
	done       chan struct{}
	stopErr    error
}

// NewSessionQueueConsumer creates a consumer; call Start to begin consuming.
func NewSessionQueueConsumer(client *Client, queueID string, handler SessionHandler, options *SessionQueueConsumerOptions) (*SessionQueueConsumer, error) {
	o := SessionQueueConsumerOptions{}
	if options != nil {
		o = *options
	}
	if o.MaxConcurrentSessions <= 0 {
		o.MaxConcurrentSessions = 4
	}
	if o.TargetSessionID != "" && o.MaxConcurrentSessions != 1 {
		return nil, errors.New("daprmq: TargetSessionID requires MaxConcurrentSessions == 1")
	}
	if o.MinBackoff <= 0 {
		o.MinBackoff = time.Second
	}
	if o.MaxBackoff <= 0 {
		o.MaxBackoff = time.Minute
	}
	if o.DrainTimeout <= 0 {
		o.DrainTimeout = 30 * time.Second
	}

	c := &SessionQueueConsumer{client: client, queueID: queueID, handler: handler, options: o, sleep: sleep, done: make(chan struct{})}
	c.stopCtx, c.beginStop = context.WithCancel(context.Background())
	c.handlerCtx, c.hardStop = context.WithCancel(context.Background())
	return c, nil
}

// Start begins consuming in the background. Cancelling ctx stops the consumer as Stop does;
// handlers get a context carrying ctx's values that is not cancelled with it. Start after the
// first call, or after Stop, does nothing.
func (c *SessionQueueConsumer) Start(ctx context.Context) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.started || c.stopCtx.Err() != nil {
		return
	}
	c.started = true
	c.handlerCtx, c.hardStop = context.WithCancel(context.WithoutCancel(ctx))

	c.slots.Add(c.options.MaxConcurrentSessions)
	for range c.options.MaxConcurrentSessions {
		go c.runSlot()
	}
	go func() {
		select {
		case <-ctx.Done():
			c.stop()
		case <-c.stopCtx.Done():
		}
	}()
}

// Stop stops claiming sessions, lets in-flight handlers finish and settle for up to DrainTimeout,
// then closes the streams, which releases their sessions. Unsettled prefetched messages return
// with their sessions. It returns context.DeadlineExceeded if handlers had to be cancelled, or
// ctx's error if ctx ends first. Calling Stop more than once is harmless.
func (c *SessionQueueConsumer) Stop(ctx context.Context) error {
	c.stop()
	select {
	case <-c.done:
		return c.stopErr
	case <-ctx.Done():
		c.hardStop()
		return ctx.Err()
	}
}

// Done is closed once the consumer has fully stopped.
func (c *SessionQueueConsumer) Done() <-chan struct{} { return c.done }

func (c *SessionQueueConsumer) stop() {
	c.stopOnce.Do(func() {
		c.mu.Lock()
		c.beginStop()
		hardStop := c.hardStop
		c.mu.Unlock()

		go func() {
			slotsDone := make(chan struct{})
			go func() {
				c.slots.Wait()
				close(slotsDone)
			}()
			timer := time.NewTimer(c.options.DrainTimeout)
			defer timer.Stop()
			select {
			case <-slotsDone:
			case <-timer.C:
				c.stopErr = context.DeadlineExceeded
				hardStop()
				<-slotsDone
			}
			hardStop()
			close(c.done)
		}()
	})
}

func (c *SessionQueueConsumer) runSlot() {
	defer c.slots.Done()
	backoff := c.options.MinBackoff
	for c.stopCtx.Err() == nil {
		if c.consumeOneSession() {
			backoff = c.options.MinBackoff
			continue
		}
		if c.stopCtx.Err() != nil || c.sleep(c.stopCtx, backoff) != nil {
			return
		}
		backoff = min(backoff*2, c.options.MaxBackoff)
	}
}

type received struct {
	delivery *SessionDelivery
	err      error
}

// consumeOneSession runs one stream to its end and reports whether a session was claimed (so the
// slot retries at once rather than backing off).
func (c *SessionQueueConsumer) consumeOneSession() (claimed bool) {
	stream, err := c.client.ConsumeSession(c.handlerCtx, c.queueID, &ConsumeSessionOptions{
		SessionID:          c.options.TargetSessionID,
		LeaseDuration:      c.options.LeaseDuration,
		PrefetchCount:      c.options.PrefetchCount,
		SessionIdleTimeout: c.options.SessionIdleTimeout,
	})
	if err != nil {
		return false
	}

	// Receive blocks, so it runs on its own goroutine; the slot also watches for Stop.
	deliveries := make(chan received)
	go func() {
		for {
			d, err := stream.Receive()
			deliveries <- received{d, err}
			if err != nil {
				return
			}
		}
	}()

	streamEnded := false
	defer func() {
		// Half-close, then wait for the server to apply what was sent and end the stream, so the
		// session is released by the time the slot moves on. Prefetched messages are left unsettled.
		_ = stream.Close()
		for !streamEnded {
			streamEnded = (<-deliveries).err != nil
		}
	}()

	for {
		if c.stopCtx.Err() != nil {
			return true // don't start another message once stopping, even if one is ready
		}
		select {
		case <-c.stopCtx.Done():
			return true
		case r := <-deliveries:
			if r.err != nil {
				streamEnded = true
				return claimed || claimedDespite(r.err)
			}
			claimed = true
			if abandon := c.handle(r.delivery); abandon {
				return true
			}
		}
	}
}

// claimedDespite: a stream that ended this way had claimed its session.
func claimedDespite(err error) bool {
	if errors.Is(err, io.EOF) {
		return true // ended cleanly after a claim (drained)
	}
	var mqErr *Error
	return errors.As(err, &mqErr) && mqErr.Code == CodeSessionLost
}

// handle runs the handler and settles the message; it reports whether to abandon the session.
func (c *SessionQueueConsumer) handle(d *SessionDelivery) (abandon bool) {
	err := c.handler(c.handlerCtx, SessionMessage{
		QueueID: c.queueID, SessionID: d.SessionID, LockID: d.LockID, Item: d.Item, Priority: d.Priority,
	})
	if err == nil {
		_ = d.Ack()
		return false
	}
	if c.stopCtx.Err() != nil {
		return true // stopping: leave it to return with the session
	}

	switch c.options.OnHandlerError {
	case NackMessage:
		_ = d.Nack()
		return false
	case AbandonSession:
		return true
	case DeadLetterAndAbandonSession:
		_ = d.DeadLetter()
		return true
	default:
		_ = d.DeadLetter()
		return false
	}
}
