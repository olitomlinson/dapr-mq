package daprmq

import (
	"context"
	"errors"
	"slices"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/olitomlinson/dapr-mq/sdks/go/internal/pb"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
)

// queueStreams answers each Consume call with the next scripted stream; once the script runs
// out, the last one repeats. A stream sends its deliveries, then either breaks or records
// settlements until the client half-closes.
type queueStreams struct {
	mu      sync.Mutex
	scripts []streamScript
	starts  chan *pb.ConsumeStart
	settled chan *pb.ConsumeRequest
	calls   atomic.Int32
}

type streamScript struct {
	frames []*pb.ConsumeResponse
	broken bool // end the stream with an error straight after the frames
}

func newQueueStreams(scripts ...streamScript) *queueStreams {
	return &queueStreams{scripts: scripts, starts: make(chan *pb.ConsumeStart, 16), settled: make(chan *pb.ConsumeRequest, 64)}
}

func deliveries(lockIDs ...string) streamScript {
	s := streamScript{}
	for _, l := range lockIDs {
		s.frames = append(s.frames, queueDelivered(l, `{"lock":"`+l+`"}`, 1))
	}
	return s
}

func (q *queueStreams) consume(stream pb.DaprMQ_ConsumeServer) error {
	first, err := stream.Recv()
	if err != nil {
		return err
	}
	q.starts <- first.GetStart()
	q.mu.Lock()
	script := q.scripts[min(int(q.calls.Add(1))-1, len(q.scripts)-1)]
	q.mu.Unlock()
	for _, f := range script.frames {
		if err := stream.Send(f); err != nil {
			return err
		}
	}
	if script.broken {
		return status.Error(codes.Unavailable, "stream broke")
	}
	for {
		req, err := stream.Recv()
		if err != nil {
			return nil
		}
		q.settled <- req
	}
}

func (q *queueStreams) nextSettled(t *testing.T) *pb.ConsumeRequest {
	t.Helper()
	select {
	case req := <-q.settled:
		return req
	case <-time.After(5 * time.Second):
		t.Fatal("no settlement frame")
		return nil
	}
}

func newQueueConsumer(t *testing.T, server *queueStreams, options *QueueConsumerOptions, handler QueueHandler) (*QueueConsumer, *recordedSleeps) {
	t.Helper()
	client := newGRPCClient(t, &fakeServer{consumeQueue: server.consume}, nil)
	consumer := NewQueueConsumer(client, "q", handler, options)
	sleeps := &recordedSleeps{}
	consumer.sleep = sleeps.sleep
	t.Cleanup(func() { _ = consumer.Stop(context.Background()) })
	return consumer, sleeps
}

func TestQueueConsumerOpensTheStreamWithTheDefaults(t *testing.T) {
	server := newQueueStreams(deliveries())
	consumer, _ := newQueueConsumer(t, server, nil, func(context.Context, QueueMessage) error { return nil })
	consumer.Start(bg)

	start := <-server.starts
	if start.QueueId != "q" || start.PrefetchCount != 100 || start.LockTtlSeconds != 30 || !start.AllowCompetingConsumers {
		t.Fatalf("start = %+v", start)
	}
}

func TestQueueConsumerAcksOnSuccessAndPassesTheDelivery(t *testing.T) {
	server := newQueueStreams(streamScript{frames: []*pb.ConsumeResponse{queueDelivered("L1", `{"n":1}`, 3)}})
	got := make(chan QueueMessage, 1)
	consumer, _ := newQueueConsumer(t, server, nil, func(_ context.Context, msg QueueMessage) error {
		got <- msg
		return nil
	})
	consumer.Start(bg)

	if ack := server.nextSettled(t).GetAck(); ack.GetLockId() != "L1" {
		t.Fatalf("want an ack of L1, got %+v", ack)
	}
	msg := <-got
	if msg.QueueID != "q" || msg.LockID != "L1" || string(msg.Item) != `{"n":1}` || msg.Priority != 1 || msg.DeliveryCount != 3 {
		t.Fatalf("msg = %+v", msg)
	}
}

func TestQueueConsumerNacksAFailedMessageByDefault(t *testing.T) {
	server := newQueueStreams(deliveries("L1"))
	consumer, _ := newQueueConsumer(t, server, nil, func(context.Context, QueueMessage) error { return errors.New("boom") })
	consumer.Start(bg)

	if nack := server.nextSettled(t).GetNack(); nack.GetLockId() != "L1" {
		t.Fatalf("want a nack of L1, got %+v", nack)
	}
}

func TestQueueConsumerDeadLettersAFailedMessageWhenConfigured(t *testing.T) {
	server := newQueueStreams(deliveries("L1"))
	consumer, _ := newQueueConsumer(t, server, &QueueConsumerOptions{OnHandlerError: DeadLetterQueueMessage},
		func(context.Context, QueueMessage) error { return errors.New("boom") })
	consumer.Start(bg)

	if dl := server.nextSettled(t).GetDeadLetter(); dl.GetLockId() != "L1" {
		t.Fatalf("want a dead-letter of L1, got %+v", dl)
	}
}

func TestQueueConsumerPacesNacksAtMaxRetriableErrorsPerSec(t *testing.T) {
	server := newQueueStreams(deliveries("L1", "L2", "L3"))
	consumer, sleeps := newQueueConsumer(t, server, &QueueConsumerOptions{StrictOrder: true},
		func(context.Context, QueueMessage) error { return errors.New("boom") })
	consumer.Start(bg)
	for range 3 {
		server.nextSettled(t)
	}

	// The first nack goes straight away; each later one waits for its own slot, 100 ms after the
	// previous one (the recorded sleep doesn't pass that time).
	waits := sleeps.waitFor(t, 2)
	if waits[0] < 50*time.Millisecond || waits[0] > 100*time.Millisecond || waits[1] < 150*time.Millisecond || waits[1] > 200*time.Millisecond {
		t.Fatalf("pacing waits = %v", waits)
	}
}

func TestQueueConsumerNeverExceedsMaxConcurrentHandlers(t *testing.T) {
	server := newQueueStreams(deliveries("L1", "L2", "L3", "L4", "L5", "L6"))
	var running, peak atomic.Int32
	consumer, _ := newQueueConsumer(t, server, &QueueConsumerOptions{MaxConcurrentHandlers: 2}, func(context.Context, QueueMessage) error {
		n := running.Add(1)
		for {
			p := peak.Load()
			if n <= p || peak.CompareAndSwap(p, n) {
				break
			}
		}
		time.Sleep(30 * time.Millisecond)
		running.Add(-1)
		return nil
	})
	consumer.Start(bg)
	for range 6 {
		server.nextSettled(t)
	}

	if peak.Load() != 2 {
		t.Fatalf("peak concurrent handlers = %d", peak.Load())
	}
}

func TestQueueConsumerStrictOrderUsesAWindowOfOneWithoutCompetingConsumers(t *testing.T) {
	server := newQueueStreams(deliveries("L1", "L2", "L3"))
	var running, peak atomic.Int32
	consumer, _ := newQueueConsumer(t, server, &QueueConsumerOptions{StrictOrder: true}, func(context.Context, QueueMessage) error {
		peak.Store(max(peak.Load(), running.Add(1)))
		time.Sleep(10 * time.Millisecond)
		running.Add(-1)
		return nil
	})
	consumer.Start(bg)

	start := <-server.starts
	var order []string
	for range 3 {
		order = append(order, server.nextSettled(t).GetAck().GetLockId())
	}
	if start.PrefetchCount != 1 || start.AllowCompetingConsumers || peak.Load() != 1 || !slices.Equal(order, []string{"L1", "L2", "L3"}) {
		t.Fatalf("start = %+v, peak = %d, order = %v", start, peak.Load(), order)
	}
}

func TestQueueConsumerStopLetsARunningHandlerAckAndStartsNoMore(t *testing.T) {
	server := newQueueStreams(deliveries("L1", "L2"))
	entered := make(chan string, 2)
	consumer, _ := newQueueConsumer(t, server, &QueueConsumerOptions{MaxConcurrentHandlers: 1}, func(_ context.Context, msg QueueMessage) error {
		entered <- msg.LockID
		time.Sleep(100 * time.Millisecond)
		return nil
	})
	consumer.Start(bg)
	<-entered

	if err := consumer.Stop(bg); err != nil {
		t.Fatal(err)
	}

	select {
	case got := <-server.settled:
		if got.GetAck().GetLockId() != "L1" {
			t.Fatalf("settled = %+v", got)
		}
	default:
		t.Fatal("Stop returned before the running message was acked")
	}
	if len(entered) != 0 || len(server.settled) != 0 {
		t.Fatal("a prefetched message was started after Stop")
	}
}

func TestQueueConsumerStopPastTheDrainTimeoutCancelsTheHandler(t *testing.T) {
	server := newQueueStreams(deliveries("L1"))
	entered := make(chan struct{})
	consumer, _ := newQueueConsumer(t, server, &QueueConsumerOptions{DrainTimeout: 50 * time.Millisecond},
		func(ctx context.Context, _ QueueMessage) error {
			close(entered)
			<-ctx.Done()
			return ctx.Err()
		})
	consumer.Start(bg)
	<-entered

	if err := consumer.Stop(bg); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("want context.DeadlineExceeded, got %v", err)
	}
}

func TestQueueConsumerReopensABrokenStreamBackingOffUntilADeliveryResetsIt(t *testing.T) {
	broken := streamScript{broken: true}
	deliveringThenBroken := deliveries("L1")
	deliveringThenBroken.broken = true
	server := newQueueStreams(broken, broken, deliveringThenBroken, broken, deliveries())
	consumer, sleeps := newQueueConsumer(t, server, &QueueConsumerOptions{MinBackoff: time.Second, MaxBackoff: time.Minute},
		func(context.Context, QueueMessage) error { return nil })
	consumer.Start(bg)

	got := sleeps.waitFor(t, 4)
	want := []time.Duration{time.Second, 2 * time.Second, time.Second, 2 * time.Second}
	if !slices.Equal(got, want) {
		t.Fatalf("backoff = %v, want %v", got, want)
	}
}
