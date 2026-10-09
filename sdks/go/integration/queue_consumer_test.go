package integration

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net"
	"slices"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	daprmq "github.com/olitomlinson/dapr-mq/sdks/go"
)

func msgSeq(t *testing.T, msg daprmq.QueueMessage) int {
	t.Helper()
	var v struct{ Seq int }
	if err := json.Unmarshal(msg.Item, &v); err != nil {
		t.Error(err)
	}
	return v.Seq
}

func waitUntil(t *testing.T, because string, cond func() bool) {
	t.Helper()
	deadline := time.Now().Add(30 * time.Second)
	for !cond() {
		if time.Now().After(deadline) {
			t.Fatalf("timed out waiting for %s", because)
		}
		time.Sleep(50 * time.Millisecond)
	}
}

func startConsumer(t *testing.T, client *daprmq.Client, queueID string, options *daprmq.QueueConsumerOptions, handler daprmq.QueueHandler) *daprmq.QueueConsumer {
	t.Helper()
	consumer := daprmq.NewQueueConsumer(client, queueID, handler, options)
	consumer.Start(context.Background())
	t.Cleanup(func() { _ = consumer.Stop(context.Background()) })
	return consumer
}

func assertEmpty(t *testing.T, client *daprmq.Client, queueID string) {
	t.Helper()
	left, err := client.DequeueLocked(context.Background(), queueID, &daprmq.DequeueLockedOptions{AllowCompetingConsumers: true})
	if err != nil || len(left.Items) != 0 {
		t.Fatalf("queue should be empty: %+v %v", left, err)
	}
}

func TestQC01_Handler_Success_Acks_And_The_Queue_Ends_Empty(t *testing.T) {
	client := newClient(t, nil)
	queueID := newQueueID()
	enqueueSeqs(t, client, queueID, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10)

	var mu sync.Mutex
	var handled []int
	consumer := startConsumer(t, client, queueID, nil, func(_ context.Context, msg daprmq.QueueMessage) error {
		mu.Lock()
		handled = append(handled, msgSeq(t, msg))
		mu.Unlock()
		return nil
	})
	waitUntil(t, "every message to be handled", func() bool { mu.Lock(); defer mu.Unlock(); return len(handled) == 10 })
	if err := consumer.Stop(context.Background()); err != nil {
		t.Fatal(err)
	}

	slices.Sort(handled)
	if !slices.Equal(handled, []int{1, 2, 3, 4, 5, 6, 7, 8, 9, 10}) {
		t.Fatalf("handled = %v", handled)
	}
	assertEmpty(t, client, queueID)
}

func TestQC02_Handler_Error_Nack_Redelivers_With_Delivery_Count_2(t *testing.T) {
	client := newClient(t, nil)
	queueID := newQueueID()
	enqueueSeqs(t, client, queueID, 1)

	counts := make(chan int, 4)
	consumer := startConsumer(t, client, queueID, nil, func(_ context.Context, msg daprmq.QueueMessage) error {
		counts <- msg.DeliveryCount
		if msg.DeliveryCount == 1 {
			return errors.New("first attempt fails")
		}
		return nil
	})
	waitUntil(t, "the nacked message to be redelivered", func() bool { return len(counts) == 2 })
	if err := consumer.Stop(context.Background()); err != nil {
		t.Fatal(err)
	}

	if first, second := <-counts, <-counts; first != 1 || second != 2 {
		t.Fatalf("delivery counts = %d, %d", first, second)
	}
	assertEmpty(t, client, queueID)
}

func TestQC02_Handler_Error_DeadLetter_Moves_It_To_The_Dead_Letter_Queue(t *testing.T) {
	client := newClient(t, nil)
	queueID := newQueueID()
	enqueueSeqs(t, client, queueID, 7)

	startConsumer(t, client, queueID, &daprmq.QueueConsumerOptions{OnHandlerError: daprmq.DeadLetterQueueMessage},
		func(context.Context, daprmq.QueueMessage) error { return errors.New("poison") })

	var dead daprmq.DequeueLockedResult
	waitUntil(t, "the message to reach the dead-letter queue", func() bool {
		var err error
		dead, err = client.DequeueLocked(context.Background(), queueID+"-deadletter", nil)
		return err == nil && len(dead.Items) == 1
	})
}

func TestQC03_MaxConcurrentHandlers_Is_Never_Exceeded(t *testing.T) {
	client := newClient(t, nil)
	queueID := newQueueID()
	enqueueSeqs(t, client, queueID, 1, 2, 3, 4, 5, 6, 7, 8)

	var running, peak, handled atomic.Int32
	var mu sync.Mutex
	startConsumer(t, client, queueID, &daprmq.QueueConsumerOptions{MaxConcurrentHandlers: 2}, func(context.Context, daprmq.QueueMessage) error {
		mu.Lock()
		peak.Store(max(peak.Load(), running.Add(1)))
		mu.Unlock()
		time.Sleep(100 * time.Millisecond)
		running.Add(-1)
		handled.Add(1)
		return nil
	})
	waitUntil(t, "every message to be handled", func() bool { return handled.Load() == 8 })

	if peak.Load() != 2 {
		t.Fatalf("peak concurrent handlers = %d", peak.Load())
	}
}

func TestQC04_StrictOrder_Handles_In_Queue_Order_Including_After_A_Nack(t *testing.T) {
	client := newClient(t, nil)
	queueID := newQueueID()
	enqueueSeqs(t, client, queueID, 1, 2, 3, 4, 5)

	var mu sync.Mutex
	var succeeded []int
	failedOnce := false
	startConsumer(t, client, queueID, &daprmq.QueueConsumerOptions{StrictOrder: true}, func(_ context.Context, msg daprmq.QueueMessage) error {
		mu.Lock()
		defer mu.Unlock()
		seq := msgSeq(t, msg)
		if seq == 2 && !failedOnce {
			failedOnce = true
			return errors.New("nack 2 once")
		}
		succeeded = append(succeeded, seq)
		return nil
	})
	waitUntil(t, "every message to be handled", func() bool { mu.Lock(); defer mu.Unlock(); return len(succeeded) == 5 })

	if !slices.Equal(succeeded, []int{1, 2, 3, 4, 5}) {
		t.Fatalf("order = %v", succeeded)
	}
}

func TestQC05_Stop_Drains_Running_Handlers_And_Returns_Unstarted_Messages_Straight_Away(t *testing.T) {
	client := newClient(t, nil)
	queueID := newQueueID()
	enqueueSeqs(t, client, queueID, 1, 2, 3)

	started := make(chan struct{}, 3)
	var handled atomic.Int32
	consumer := startConsumer(t, client, queueID,
		&daprmq.QueueConsumerOptions{MaxConcurrentHandlers: 1, MaxActiveMessages: 10, LockTTL: 300 * time.Second},
		func(context.Context, daprmq.QueueMessage) error {
			started <- struct{}{}
			time.Sleep(500 * time.Millisecond)
			handled.Add(1)
			return nil
		})
	<-started
	if err := consumer.Stop(context.Background()); err != nil {
		t.Fatal(err)
	}

	if handled.Load() != 1 || len(started) != 0 {
		t.Fatalf("handled %d, started after stop %d", handled.Load(), len(started))
	}
	// Well inside the 300 s lock, so only the stream's close can have returned them.
	back, err := client.DequeueLocked(context.Background(), queueID, &daprmq.DequeueLockedOptions{Count: 10, AllowCompetingConsumers: true})
	if err != nil {
		t.Fatal(err)
	}
	if got := seqs(t, back.Items); !slices.Equal(got, []int{2, 3}) {
		t.Fatalf("returned = %v", got)
	}
}

func TestQC06_Broken_Stream_Reconnects_And_Every_Message_Is_Handled_At_Least_Once(t *testing.T) {
	proxy := newTCPProxy(t, grpcAddress)
	client, err := daprmq.NewClient(httpURL, proxy.addr(), nil)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = client.Close() })
	queueID := newQueueID()
	all := make([]int, 20)
	for i := range all {
		all[i] = i + 1
	}
	enqueueSeqs(t, client, queueID, all...)

	var mu sync.Mutex
	handled := map[int]int{}
	count := func() int { mu.Lock(); defer mu.Unlock(); return len(handled) }
	startConsumer(t, client, queueID, &daprmq.QueueConsumerOptions{MaxActiveMessages: 5, MaxConcurrentHandlers: 2},
		func(_ context.Context, msg daprmq.QueueMessage) error {
			time.Sleep(50 * time.Millisecond)
			mu.Lock()
			handled[msgSeq(t, msg)]++
			mu.Unlock()
			return nil
		})
	waitUntil(t, "some messages to be handled before the break", func() bool { return count() >= 5 })
	proxy.breakConnections()
	waitUntil(t, "every message to be handled after reconnecting", func() bool { return count() == 20 })

	assertEmpty(t, client, queueID)
}

// tcpProxy forwards a local port to the server so QC-06 can break every open connection without
// restarting a container, which would re-map its host ports.
type tcpProxy struct {
	listener net.Listener
	mu       sync.Mutex
	open     []net.Conn
}

func newTCPProxy(t *testing.T, target string) *tcpProxy {
	t.Helper()
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	p := &tcpProxy{listener: l}
	go func() {
		for {
			in, err := l.Accept()
			if err != nil {
				return
			}
			out, err := net.Dial("tcp", target)
			if err != nil {
				_ = in.Close()
				continue
			}
			p.mu.Lock()
			p.open = append(p.open, in, out)
			p.mu.Unlock()
			go pipe(in, out)
			go pipe(out, in)
		}
	}()
	t.Cleanup(func() {
		_ = l.Close()
		p.breakConnections()
	})
	return p
}

func (p *tcpProxy) addr() string { return p.listener.Addr().String() }

// breakConnections drops every connection open now; new ones are still accepted.
func (p *tcpProxy) breakConnections() {
	p.mu.Lock()
	defer p.mu.Unlock()
	for _, c := range p.open {
		if tcp, ok := c.(*net.TCPConn); ok {
			_ = tcp.SetLinger(0) // reset, not a clean close
		}
		_ = c.Close()
	}
	p.open = nil
}

func pipe(from, to net.Conn) {
	_, _ = io.Copy(to, from)
	_ = from.Close()
	_ = to.Close()
}
