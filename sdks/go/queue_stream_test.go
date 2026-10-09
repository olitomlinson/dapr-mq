package daprmq

import (
	"errors"
	"io"
	"testing"
	"time"

	"github.com/olitomlinson/dapr-mq/sdks/go/internal/pb"
)

// queueScript scripts a Consume server: it records the Start frame, sends the given frames, then
// records every settlement frame until the client half-closes.
type queueScript struct {
	start   chan *pb.ConsumeStart
	settled chan *pb.ConsumeRequest
}

func newQueueScript() *queueScript {
	return &queueScript{start: make(chan *pb.ConsumeStart, 4), settled: make(chan *pb.ConsumeRequest, 64)}
}

func (s *queueScript) serve(frames ...*pb.ConsumeResponse) func(pb.DaprMQ_ConsumeServer) error {
	return func(stream pb.DaprMQ_ConsumeServer) error {
		first, err := stream.Recv()
		if err != nil {
			return err
		}
		s.start <- first.GetStart()
		for _, f := range frames {
			if err := stream.Send(f); err != nil {
				return err
			}
		}
		for {
			req, err := stream.Recv()
			if err != nil {
				return nil
			}
			s.settled <- req
		}
	}
}

func (s *queueScript) next(t *testing.T) *pb.ConsumeRequest {
	t.Helper()
	select {
	case req := <-s.settled:
		return req
	case <-time.After(5 * time.Second):
		t.Fatal("no settlement frame")
		return nil
	}
}

func queueDelivered(lockID, itemJSON string, deliveryCount int32) *pb.ConsumeResponse {
	return &pb.ConsumeResponse{Payload: &pb.ConsumeResponse_Delivered{
		Delivered: &pb.ConsumeDelivered{LockId: lockID, ItemJson: itemJSON, Priority: 1, LockExpiresAt: 1700000030, DeliveryCount: deliveryCount},
	}}
}

func TestConsumeSendsStartAndYieldsDeliveriesWhoseSettlesSendFrames(t *testing.T) {
	script := newQueueScript()
	client := newGRPCClient(t, &fakeServer{consumeQueue: script.serve(
		queueDelivered("L1", `{"n":1}`, 1), queueDelivered("L2", `{"n":2}`, 3), queueDelivered("L3", `{"n":3}`, 1),
	)}, nil)

	stream, err := client.Consume(bg, "q", &ConsumeOptions{PrefetchCount: 50, LockTTL: 45 * time.Second, AllowCompetingConsumers: true})
	if err != nil {
		t.Fatal(err)
	}
	defer stream.Close()

	start := <-script.start
	if start.QueueId != "q" || start.PrefetchCount != 50 || start.LockTtlSeconds != 45 || !start.AllowCompetingConsumers {
		t.Fatalf("start = %+v", start)
	}

	d1, err := stream.Receive()
	if err != nil {
		t.Fatal(err)
	}
	if d1.LockID != "L1" || string(d1.Item) != `{"n":1}` || d1.Priority != 1 || d1.DeliveryCount != 1 || !d1.LockExpiresAt.Equal(time.Unix(1700000030, 0)) {
		t.Fatalf("delivery = %+v", d1)
	}
	d2, _ := stream.Receive()
	d3, _ := stream.Receive()
	if d2.DeliveryCount != 3 {
		t.Fatalf("delivery count = %d", d2.DeliveryCount)
	}

	_ = d1.Ack()
	_ = d2.Nack()
	_ = d3.DeadLetter()
	if got := script.next(t); got.GetAck().GetLockId() != "L1" {
		t.Fatalf("settled = %+v", got)
	}
	if got := script.next(t); got.GetNack().GetLockId() != "L2" {
		t.Fatalf("settled = %+v", got)
	}
	if got := script.next(t); got.GetDeadLetter().GetLockId() != "L3" {
		t.Fatalf("settled = %+v", got)
	}
}

func TestConsumeDefaults(t *testing.T) {
	script := newQueueScript()
	client := newGRPCClient(t, &fakeServer{consumeQueue: script.serve()}, nil)

	stream, err := client.Consume(bg, "q", nil)
	if err != nil {
		t.Fatal(err)
	}
	defer stream.Close()

	start := <-script.start
	if start.PrefetchCount != 1 || start.LockTtlSeconds != 30 || start.AllowCompetingConsumers {
		t.Fatalf("start = %+v", start)
	}
}

func TestASettleFailedFrameIsReportedAndTheStreamCarriesOn(t *testing.T) {
	script := newQueueScript()
	client := newGRPCClient(t, &fakeServer{consumeQueue: script.serve(
		&pb.ConsumeResponse{Payload: &pb.ConsumeResponse_SettleFailed{SettleFailed: &pb.ConsumeSettleFailed{LockId: "L0", ErrorCode: "LOCK_NOT_FOUND", Message: "gone"}}},
		queueDelivered("L1", `1`, 1),
	)}, nil)

	type failure struct {
		lockID string
		err    error
	}
	failures := make(chan failure, 1)
	stream, err := client.Consume(bg, "q", &ConsumeOptions{OnSettleFailed: func(lockID string, err error) { failures <- failure{lockID, err} }})
	if err != nil {
		t.Fatal(err)
	}
	defer stream.Close()

	d, err := stream.Receive()
	if err != nil || d.LockID != "L1" {
		t.Fatalf("delivery = %+v, err = %v", d, err)
	}
	f := <-failures
	if f.lockID != "L0" {
		t.Fatalf("lock = %s", f.lockID)
	}
	requireCode(t, f.err, CodeLockNotFound)
}

func TestAConsumeErrorFrameEndsTheStreamWithTheMappedError(t *testing.T) {
	client := newGRPCClient(t, &fakeServer{consumeQueue: newQueueScript().serve(
		&pb.ConsumeResponse{Payload: &pb.ConsumeResponse_Error{Error: &pb.ConsumeError{ErrorCode: "INVALID_ARGUMENT", Message: "bad"}}},
	)}, nil)
	stream, err := client.Consume(bg, "q", nil)
	if err != nil {
		t.Fatal(err)
	}
	defer stream.Close()

	_, err = stream.Receive()
	requireCode(t, err, "INVALID_ARGUMENT")
	if _, again := stream.Receive(); !errors.Is(again, err) {
		t.Fatalf("Receive keeps returning the terminal error, got %v", again)
	}
}

func TestQueueStreamCloseHalfClosesAndEndsWithEOF(t *testing.T) {
	script := newQueueScript()
	client := newGRPCClient(t, &fakeServer{consumeQueue: script.serve(queueDelivered("L1", `1`, 1))}, nil)
	stream, err := client.Consume(bg, "q", nil)
	if err != nil {
		t.Fatal(err)
	}

	d, _ := stream.Receive()
	_ = d.Ack()
	if err := stream.Close(); err != nil {
		t.Fatal(err)
	}

	if got := script.next(t); got.GetAck().GetLockId() != "L1" {
		t.Fatalf("settled = %+v", got)
	}
	if _, err := stream.Receive(); !errors.Is(err, io.EOF) {
		t.Fatalf("Receive after the server ends = %v, want io.EOF", err)
	}
	if err := d.Ack(); !errors.Is(err, ErrStreamClosed) {
		t.Fatalf("settling after Close = %v, want ErrStreamClosed", err)
	}
}
