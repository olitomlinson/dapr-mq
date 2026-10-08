package daprmq

import (
	"context"
	"errors"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/olitomlinson/dapr-mq/sdks/go/internal/pb"
)

// sessionsServer answers each ConsumeSession call with the next scripted session; once the script
// runs out, every call gets NO_SESSIONS_AVAILABLE. A scripted session sends its deliveries,
// records settlements, and drains once every delivery is settled.
type sessionsServer struct {
	mu       sync.Mutex
	sessions [][]*pb.ConsumeSessionResponse
	calls    atomic.Int32
	settled  chan *pb.ConsumeSessionRequest
}

func newSessionsServer(sessions ...[]*pb.ConsumeSessionResponse) *sessionsServer {
	return &sessionsServer{sessions: sessions, settled: make(chan *pb.ConsumeSessionRequest, 64)}
}

func (s *sessionsServer) consume(stream pb.DaprMQ_ConsumeSessionServer) error {
	s.calls.Add(1)
	if _, err := stream.Recv(); err != nil {
		return err
	}
	s.mu.Lock()
	if len(s.sessions) == 0 {
		s.mu.Unlock()
		return stream.Send(sessionError("NO_SESSIONS_AVAILABLE", "none"))
	}
	frames := s.sessions[0]
	s.sessions = s.sessions[1:]
	s.mu.Unlock()

	outstanding := 0
	for _, f := range frames {
		if f.GetDelivered() != nil {
			outstanding++
		}
		if err := stream.Send(f); err != nil {
			return err
		}
	}
	for outstanding > 0 {
		req, err := stream.Recv()
		if err != nil {
			return nil
		}
		s.settled <- req
		outstanding--
	}
	return stream.Send(drained(""))
}

func session(id string, lockIDs ...string) []*pb.ConsumeSessionResponse {
	frames := []*pb.ConsumeSessionResponse{assigned(id)}
	for _, l := range lockIDs {
		frames = append(frames, delivered(l, `{"lock":"`+l+`"}`))
	}
	return frames
}

type recordedSleeps struct {
	mu     sync.Mutex
	sleeps []time.Duration
}

func (r *recordedSleeps) sleep(ctx context.Context, d time.Duration) error {
	r.mu.Lock()
	r.sleeps = append(r.sleeps, d)
	r.mu.Unlock()
	// Yield briefly so a test that waits on the record isn't racing a spinning loop.
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-time.After(time.Millisecond):
		return nil
	}
}

func (r *recordedSleeps) waitFor(t *testing.T, n int) []time.Duration {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		r.mu.Lock()
		if len(r.sleeps) >= n {
			got := append([]time.Duration(nil), r.sleeps[:n]...)
			r.mu.Unlock()
			return got
		}
		r.mu.Unlock()
		time.Sleep(5 * time.Millisecond)
	}
	t.Fatalf("fewer than %d backoff sleeps recorded", n)
	return nil
}

func newConsumer(t *testing.T, server *sessionsServer, options *SessionQueueConsumerOptions, handler SessionHandler) (*SessionQueueConsumer, *recordedSleeps) {
	t.Helper()
	client := newGRPCClient(t, &fakeServer{consume: server.consume}, nil)
	consumer, err := NewSessionQueueConsumer(client, "q", handler, options)
	if err != nil {
		t.Fatal(err)
	}
	sleeps := &recordedSleeps{}
	consumer.sleep = sleeps.sleep
	t.Cleanup(func() { _ = consumer.Stop(context.Background()) })
	return consumer, sleeps
}

func oneSlot() *SessionQueueConsumerOptions {
	return &SessionQueueConsumerOptions{MaxConcurrentSessions: 1}
}

func TestConsumerClaimsDeliversHandlesAndAcksOnTheHappyPath(t *testing.T) {
	server := newSessionsServer(session("s1", "L1"))
	handled := make(chan SessionMessage, 1)
	consumer, _ := newConsumer(t, server, oneSlot(), func(ctx context.Context, msg SessionMessage) error {
		handled <- msg
		return nil
	})

	consumer.Start(bg)

	msg := <-handled
	if msg.QueueID != "q" || msg.SessionID != "s1" || msg.LockID != "L1" || string(msg.Item) != `{"lock":"L1"}` || msg.Priority != 1 {
		t.Fatalf("msg = %+v", msg)
	}
	if got := <-server.settled; got.GetAck().GetLockId() != "L1" {
		t.Fatalf("settled = %+v", got)
	}
}

func TestConsumerBacksOffDoublingOnRepeatedNoSessionsAvailableThenResets(t *testing.T) {
	server := newSessionsServer()
	consumer, sleeps := newConsumer(t, server, &SessionQueueConsumerOptions{
		MaxConcurrentSessions: 1, MinBackoff: time.Second, MaxBackoff: time.Minute,
	}, func(context.Context, SessionMessage) error { return nil })

	consumer.Start(bg)
	got := sleeps.waitFor(t, 3)
	if got[0] != time.Second || got[1] != 2*time.Second || got[2] != 4*time.Second {
		t.Fatalf("sleeps = %v", got)
	}

	server.mu.Lock()
	server.sessions = append(server.sessions, session("s1", "L1"))
	server.mu.Unlock()
	<-server.settled

	// After the successful claim the next miss backs off from the minimum again.
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		sleeps.mu.Lock()
		all := append([]time.Duration(nil), sleeps.sleeps...)
		sleeps.mu.Unlock()
		for i := 1; i < len(all); i++ {
			if all[i] == time.Second && all[i-1] > time.Second {
				return
			}
		}
		time.Sleep(5 * time.Millisecond)
	}
	t.Fatal("backoff never reset after a successful claim")
}

func TestConsumerCapsBackoffAtMaxBackoff(t *testing.T) {
	consumer, sleeps := newConsumer(t, newSessionsServer(), &SessionQueueConsumerOptions{
		MaxConcurrentSessions: 1, MinBackoff: time.Second, MaxBackoff: 3 * time.Second,
	}, func(context.Context, SessionMessage) error { return nil })

	consumer.Start(bg)

	got := sleeps.waitFor(t, 4)
	if got[2] != 3*time.Second || got[3] != 3*time.Second {
		t.Fatalf("sleeps = %v", got)
	}
}

func TestConsumerDeadLettersTheMessageByDefaultWhenTheHandlerFails(t *testing.T) {
	server := newSessionsServer(session("s1", "L1", "L2"))
	consumer, _ := newConsumer(t, server, oneSlot(), func(ctx context.Context, msg SessionMessage) error {
		if msg.LockID == "L1" {
			return errors.New("boom")
		}
		return nil
	})

	consumer.Start(bg)

	if got := <-server.settled; got.GetDeadLetter().GetLockId() != "L1" {
		t.Fatalf("settled = %+v", got)
	}
	if got := <-server.settled; got.GetAck().GetLockId() != "L2" {
		t.Fatalf("the session continues after a dead-letter: %+v", got)
	}
}

func TestConsumerNackMessageNacksInsteadOfDeadLettering(t *testing.T) {
	server := newSessionsServer(session("s1", "L1"))
	consumer, _ := newConsumer(t, server, &SessionQueueConsumerOptions{MaxConcurrentSessions: 1, OnHandlerError: NackMessage},
		func(context.Context, SessionMessage) error { return errors.New("boom") })

	consumer.Start(bg)

	if got := <-server.settled; got.GetNack().GetLockId() != "L1" {
		t.Fatalf("settled = %+v", got)
	}
}

func TestConsumerAbandonSessionDoesNotDeadLetterAndKeepsTheSlotAlive(t *testing.T) {
	server := newSessionsServer(session("s1", "L1"), session("s2", "L2"))
	handled := make(chan string, 4)
	consumer, _ := newConsumer(t, server, &SessionQueueConsumerOptions{MaxConcurrentSessions: 1, OnHandlerError: AbandonSession},
		func(ctx context.Context, msg SessionMessage) error {
			handled <- msg.LockID
			if msg.LockID == "L1" {
				return errors.New("boom")
			}
			return nil
		})

	consumer.Start(bg)

	if l := <-handled; l != "L1" {
		t.Fatal(l)
	}
	if l := <-handled; l != "L2" {
		t.Fatalf("the slot claims the next session: %s", l)
	}
	if got := <-server.settled; got.GetAck().GetLockId() != "L2" {
		t.Fatalf("L1 is abandoned, not settled: %+v", got)
	}
}

func TestConsumerBothDeadLettersAndAbandons(t *testing.T) {
	server := newSessionsServer(session("s1", "L1", "L2"), session("s2", "L3"))
	consumer, _ := newConsumer(t, server, &SessionQueueConsumerOptions{MaxConcurrentSessions: 1, OnHandlerError: DeadLetterAndAbandonSession},
		func(ctx context.Context, msg SessionMessage) error {
			if msg.LockID == "L1" {
				return errors.New("boom")
			}
			return nil
		})

	consumer.Start(bg)

	if got := <-server.settled; got.GetDeadLetter().GetLockId() != "L1" {
		t.Fatalf("settled = %+v", got)
	}
	if got := <-server.settled; got.GetAck().GetLockId() != "L3" {
		t.Fatalf("L2 stays with the abandoned session; the slot moves to s2: %+v", got)
	}
}

func TestConsumerStopLetsAnInFlightHandlerFinishAndAck(t *testing.T) {
	server := newSessionsServer(session("s1", "L1"))
	entered := make(chan struct{})
	consumer, _ := newConsumer(t, server, oneSlot(), func(ctx context.Context, msg SessionMessage) error {
		close(entered)
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
		t.Fatal("Stop returned before the in-flight message was acked")
	}
}

func TestConsumerStopPastTheDrainTimeoutCancelsTheHandler(t *testing.T) {
	server := newSessionsServer(session("s1", "L1"))
	entered := make(chan struct{})
	cancelled := make(chan struct{})
	consumer, _ := newConsumer(t, server, &SessionQueueConsumerOptions{MaxConcurrentSessions: 1, DrainTimeout: 50 * time.Millisecond},
		func(ctx context.Context, msg SessionMessage) error {
			close(entered)
			<-ctx.Done()
			close(cancelled)
			return ctx.Err()
		})
	consumer.Start(bg)
	<-entered

	started := time.Now()
	err := consumer.Stop(bg)

	if !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("want context.DeadlineExceeded, got %v", err)
	}
	if time.Since(started) > 2*time.Second {
		t.Fatal("Stop overran the drain timeout")
	}
	select {
	case <-cancelled:
	case <-time.After(2 * time.Second):
		t.Fatal("handler context was never cancelled")
	}
}

func TestConsumerStopsWhenTheStartContextIsCancelled(t *testing.T) {
	server := newSessionsServer()
	consumer, sleeps := newConsumer(t, server, oneSlot(), func(context.Context, SessionMessage) error { return nil })
	ctx, cancel := context.WithCancel(bg)

	consumer.Start(ctx)
	sleeps.waitFor(t, 1)
	cancel()

	select {
	case <-consumer.Done():
	case <-time.After(5 * time.Second):
		t.Fatal("consumer did not stop")
	}
}

func TestConsumerTargetSessionIDRequiresOneSlot(t *testing.T) {
	_, err := NewSessionQueueConsumer(&Client{}, "q", func(context.Context, SessionMessage) error { return nil },
		&SessionQueueConsumerOptions{TargetSessionID: "s1", MaxConcurrentSessions: 2})

	if err == nil {
		t.Fatal("want an error")
	}
}

func TestConsumerRunsMaxConcurrentSessionsSlots(t *testing.T) {
	server := newSessionsServer(session("s1", "L1"), session("s2", "L2"), session("s3", "L3"))
	var inHandler, peak atomic.Int32
	release := make(chan struct{})
	consumer, _ := newConsumer(t, server, &SessionQueueConsumerOptions{MaxConcurrentSessions: 2},
		func(context.Context, SessionMessage) error {
			n := inHandler.Add(1)
			for {
				p := peak.Load()
				if n <= p || peak.CompareAndSwap(p, n) {
					break
				}
			}
			<-release
			inHandler.Add(-1)
			return nil
		})

	consumer.Start(bg)
	deadline := time.Now().Add(5 * time.Second)
	for peak.Load() < 2 && time.Now().Before(deadline) {
		time.Sleep(5 * time.Millisecond)
	}
	time.Sleep(50 * time.Millisecond)
	close(release)

	if p := peak.Load(); p != 2 {
		t.Fatalf("peak concurrent handlers = %d, want 2", p)
	}
}
