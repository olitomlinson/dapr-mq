package daprmq

import (
	"context"
	"encoding/json"
	"sync"
	"time"
)

// QueueHandler handles one message from a plain queue. Returning nil acks it; returning an error
// applies [QueueConsumerOptions.OnHandlerError]. ctx is cancelled only when Stop gives up waiting
// for running handlers (DrainTimeout).
type QueueHandler func(ctx context.Context, msg QueueMessage) error

// QueueMessage is one message handed to a [QueueHandler].
type QueueMessage struct {
	QueueID  string
	LockID   string
	Item     json.RawMessage
	Priority int
	// DeliveryCount is 1 on a first delivery, 2 on the first redelivery, and so on.
	DeliveryCount int
}

// QueueHandlerFailureAction is what a [QueueConsumer] does when its handler returns an error.
type QueueHandlerFailureAction int

const (
	// NackQueueMessage returns the message to its original position for redelivery (the default).
	// It counts toward the server's max delivery count, past which it is dead-lettered.
	NackQueueMessage QueueHandlerFailureAction = iota
	// DeadLetterQueueMessage moves the message to "{queueID}-deadletter".
	DeadLetterQueueMessage
)

// QueueConsumerOptions configures a [QueueConsumer]. Zero fields use the defaults.
type QueueConsumerOptions struct {
	// MaxActiveMessages is how many delivered but unsettled messages the server keeps in flight to
	// this consumer: the stream's PrefetchCount (1-1000). Messages over MaxConcurrentHandlers wait
	// locked, and the server keeps their locks alive. Default 100.
	MaxActiveMessages int
	// MaxConcurrentHandlers is how many handlers run at once. Default 0: unlimited, which
	// MaxActiveMessages bounds.
	MaxConcurrentHandlers int
	// LockTTL is passed to the stream; the server renews each lock until its message is settled.
	// Default 30 s.
	LockTTL time.Duration
	// AllowCompetingConsumers lets replicas share the queue, each holding its own locks. Default
	// true; set it with Ptr(false) to make this the queue's only lock holder.
	AllowCompetingConsumers *bool
	// StrictOrder handles messages one at a time in queue order, including after a nack: it forces
	// a window of 1, one handler, and no competing consumers.
	StrictOrder bool
	// OnHandlerError defaults to NackQueueMessage.
	OnHandlerError QueueHandlerFailureAction
	// MaxRetriableErrorsPerSec paces nacks after handler errors, so a failing handler doesn't spin.
	// Default 10; negative means unpaced.
	MaxRetriableErrorsPerSec float64
	// MinBackoff and MaxBackoff bound the wait before reopening a broken stream: it doubles on each
	// break and resets after a delivery. Defaults 1 s and 60 s.
	MinBackoff time.Duration
	MaxBackoff time.Duration
	// DrainTimeout is how long Stop waits for running handlers before cancelling them. Default 30 s.
	DrainTimeout time.Duration
}

// QueueConsumer runs a handler over a plain queue's [Client.Consume] stream: success acks the
// message, an error nacks or dead-letters it, and a broken stream is reopened with backoff (the
// server has already returned whatever was unsettled on it).
type QueueConsumer struct {
	client  *Client
	queueID string
	handler QueueHandler
	options QueueConsumerOptions
	stream  ConsumeOptions

	// sleep is a test seam for the reconnect backoff and nack pacing.
	sleep func(ctx context.Context, d time.Duration) error

	paceMu   sync.Mutex
	epoch    time.Time
	nextNack time.Duration

	mu         sync.Mutex
	started    bool
	stopCtx    context.Context // cancelled when stopping begins
	beginStop  context.CancelFunc
	handlerCtx context.Context // cancelled when draining gives up
	hardStop   context.CancelFunc
	running    sync.WaitGroup
	stopOnce   sync.Once
	done       chan struct{}
	stopErr    error
}

// NewQueueConsumer creates a consumer; call Start to begin consuming.
func NewQueueConsumer(client *Client, queueID string, handler QueueHandler, options *QueueConsumerOptions) *QueueConsumer {
	o := QueueConsumerOptions{}
	if options != nil {
		o = *options
	}
	if o.MaxActiveMessages <= 0 {
		o.MaxActiveMessages = 100
	}
	if o.MaxRetriableErrorsPerSec == 0 {
		o.MaxRetriableErrorsPerSec = 10
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
	stream := ConsumeOptions{
		PrefetchCount:           o.MaxActiveMessages,
		LockTTL:                 o.LockTTL,
		AllowCompetingConsumers: o.AllowCompetingConsumers == nil || *o.AllowCompetingConsumers,
	}
	if o.StrictOrder {
		stream.PrefetchCount, stream.AllowCompetingConsumers, o.MaxConcurrentHandlers = 1, false, 1
	}

	c := &QueueConsumer{client: client, queueID: queueID, handler: handler, options: o, stream: stream, sleep: sleep,
		epoch: time.Now(), done: make(chan struct{})}
	c.stopCtx, c.beginStop = context.WithCancel(context.Background())
	c.handlerCtx, c.hardStop = context.WithCancel(context.Background())
	return c
}

// Start begins consuming in the background. Cancelling ctx stops the consumer as Stop does;
// handlers get a context carrying ctx's values that is not cancelled with it. Start after the
// first call, or after Stop, does nothing.
func (c *QueueConsumer) Start(ctx context.Context) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.started || c.stopCtx.Err() != nil {
		return
	}
	c.started = true
	c.handlerCtx, c.hardStop = context.WithCancel(context.WithoutCancel(ctx))

	c.running.Add(1)
	go c.run()
	go func() {
		select {
		case <-ctx.Done():
			c.stop()
		case <-c.stopCtx.Done():
		}
	}()
}

// Stop stops handing out messages, lets running handlers finish and settle for up to
// DrainTimeout, then closes the stream, so the server returns every message not yet handled
// straight away. It returns context.DeadlineExceeded if handlers had to be cancelled, or ctx's
// error if ctx ends first. Calling Stop more than once is harmless.
func (c *QueueConsumer) Stop(ctx context.Context) error {
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
func (c *QueueConsumer) Done() <-chan struct{} { return c.done }

func (c *QueueConsumer) stop() {
	c.stopOnce.Do(func() {
		c.mu.Lock()
		c.beginStop()
		hardStop := c.hardStop
		c.mu.Unlock()

		go func() {
			runDone := make(chan struct{})
			go func() {
				c.running.Wait()
				close(runDone)
			}()
			timer := time.NewTimer(c.options.DrainTimeout)
			defer timer.Stop()
			select {
			case <-runDone:
			case <-timer.C:
				c.stopErr = context.DeadlineExceeded
				hardStop()
				<-runDone
			}
			hardStop()
			close(c.done)
		}()
	})
}

func (c *QueueConsumer) run() {
	defer c.running.Done()
	backoff := c.options.MinBackoff
	for c.stopCtx.Err() == nil {
		delivered := c.serveStream()
		if c.stopCtx.Err() != nil {
			return
		}
		if delivered {
			backoff = c.options.MinBackoff
		}
		if c.sleep(c.stopCtx, backoff) != nil {
			return
		}
		backoff = min(backoff*2, c.options.MaxBackoff)
	}
}

// serveStream runs one stream until it breaks or the consumer stops, and reports whether anything
// was delivered. Running handlers settle before the stream is closed.
func (c *QueueConsumer) serveStream() (delivered bool) {
	stream, err := c.client.Consume(c.handlerCtx, c.queueID, &c.stream)
	if err != nil {
		return false
	}

	type received struct {
		delivery *QueueDelivery
		err      error
	}
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

	var slots chan struct{}
	if c.options.MaxConcurrentHandlers > 0 {
		slots = make(chan struct{}, c.options.MaxConcurrentHandlers)
	}
	var handlers sync.WaitGroup
	streamEnded := false
	defer func() {
		handlers.Wait()
		// Half-close, then wait for the server to end the stream: it returns what was never handled.
		_ = stream.Close()
		for !streamEnded {
			streamEnded = (<-deliveries).err != nil
		}
	}()

	for {
		select {
		case <-c.stopCtx.Done():
			return delivered
		case r := <-deliveries:
			if r.err != nil {
				streamEnded = true
				return delivered
			}
			delivered = true
			if slots != nil {
				select {
				case slots <- struct{}{}:
				case <-c.stopCtx.Done():
					return delivered // stopping: left unsettled, returned when the stream closes
				}
			}
			if c.stopCtx.Err() != nil {
				return delivered
			}
			handlers.Add(1)
			go func() {
				defer handlers.Done()
				if slots != nil {
					defer func() { <-slots }()
				}
				c.handle(r.delivery)
			}()
		}
	}
}

// handle runs the handler and settles the message. A settle that fails because the stream broke
// needs nothing more: the server returns the message.
func (c *QueueConsumer) handle(d *QueueDelivery) {
	err := c.handler(c.handlerCtx, QueueMessage{
		QueueID: c.queueID, LockID: d.LockID, Item: d.Item, Priority: d.Priority, DeliveryCount: d.DeliveryCount,
	})
	if err == nil {
		_ = d.Ack()
		return
	}
	if c.stopCtx.Err() != nil {
		return // stopping: left unsettled, returned when the stream closes
	}
	if c.options.OnHandlerError == DeadLetterQueueMessage {
		_ = d.DeadLetter()
		return
	}
	if c.paceNack() == nil {
		_ = d.Nack()
	}
}

// paceNack waits for this nack's slot: at most MaxRetriableErrorsPerSec nacks a second.
func (c *QueueConsumer) paceNack() error {
	if c.options.MaxRetriableErrorsPerSec < 0 {
		return nil
	}
	c.paceMu.Lock()
	now := time.Since(c.epoch)
	slot := max(c.nextNack, now)
	c.nextNack = slot + time.Duration(float64(time.Second)/c.options.MaxRetriableErrorsPerSec)
	c.paceMu.Unlock()
	if wait := slot - now; wait > 0 {
		return c.sleep(c.handlerCtx, wait)
	}
	return nil
}
