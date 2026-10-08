package daprmq

import (
	"context"
	"errors"
	"io"
	"testing"
	"time"

	"github.com/olitomlinson/dapr-mq/sdks/go/internal/pb"
)

func TestConsumeSessionSendsStartAndYieldsADeliveryWhoseAckSendsAnAckFrame(t *testing.T) {
	script := newConsumeScript()
	client := newGRPCClient(t, &fakeServer{consume: script.serve(assigned("s1"), delivered("L1", `{"n":1}`))}, nil)

	stream, err := client.ConsumeSession(bg, "q", &ConsumeSessionOptions{
		SessionID: "s1", LeaseDuration: 10 * time.Second, PrefetchCount: 3, SessionIdleTimeout: 5 * time.Second,
	})
	if err != nil {
		t.Fatal(err)
	}
	defer stream.Close()

	delivery, err := stream.Receive()
	if err != nil {
		t.Fatal(err)
	}
	if delivery.SessionID != "s1" || delivery.LockID != "L1" || string(delivery.Item) != `{"n":1}` || delivery.Priority != 1 ||
		!delivery.LockExpiresAt.Equal(time.Unix(1700000030, 0)) {
		t.Fatalf("delivery = %+v", delivery)
	}
	if stream.SessionID() != "s1" {
		t.Fatalf("SessionID() = %q", stream.SessionID())
	}

	start := <-script.start
	if start.QueueId != "q" || start.GetSessionId() != "s1" || start.LeaseSeconds != 10 || start.PrefetchCount != 3 || start.SessionIdleTimeoutSeconds != 5 {
		t.Fatalf("start = %+v", start)
	}

	if err := delivery.Ack(); err != nil {
		t.Fatal(err)
	}
	if got := receiveSettled(t, script); got.GetAck().GetLockId() != "L1" {
		t.Fatalf("settled = %+v", got)
	}
}

func TestConsumeSessionAnyAvailableLeavesSessionIDUnsetAndUsesDefaults(t *testing.T) {
	script := newConsumeScript()
	client := newGRPCClient(t, &fakeServer{consume: script.serve(assigned("chosen"))}, nil)

	stream, err := client.ConsumeSession(bg, "q", nil)
	if err != nil {
		t.Fatal(err)
	}
	defer stream.Close()

	start := <-script.start
	if start.SessionId != nil || start.LeaseSeconds != 30 || start.PrefetchCount != 1 || start.SessionIdleTimeoutSeconds != 0 {
		t.Fatalf("start = %+v", start)
	}
}

func TestDeadLetterAndNackSendTheirFrames(t *testing.T) {
	script := newConsumeScript()
	client := newGRPCClient(t, &fakeServer{consume: script.serve(assigned("s1"), delivered("L1", `1`), delivered("L2", `2`))}, nil)
	stream, err := client.ConsumeSession(bg, "q", nil)
	if err != nil {
		t.Fatal(err)
	}
	defer stream.Close()

	first, _ := stream.Receive()
	second, _ := stream.Receive()
	if err := first.DeadLetter(); err != nil {
		t.Fatal(err)
	}
	if err := second.Nack(); err != nil {
		t.Fatal(err)
	}

	if got := receiveSettled(t, script); got.GetDeadLetter().GetLockId() != "L1" {
		t.Fatalf("settled = %+v", got)
	}
	if got := receiveSettled(t, script); got.GetNack().GetLockId() != "L2" {
		t.Fatalf("settled = %+v", got)
	}
}

func TestAnErrorFrameReturnsTheMappedError(t *testing.T) {
	cases := map[string]Code{
		"SESSION_NOT_FOUND":         CodeSessionNotFound,
		"SESSION_LOCKED":            CodeSessionLocked,
		"NO_SESSIONS_AVAILABLE":     CodeNoSessionsAvailable,
		"SESSION_ACTOR_UNAVAILABLE": CodeSessionActorUnavailable,
		"ACK_FAILED":                Code("ACK_FAILED"),
	}
	for wire, want := range cases {
		t.Run(wire, func(t *testing.T) {
			script := newConsumeScript()
			client := newGRPCClient(t, &fakeServer{consume: script.serve(sessionError(wire, "nope"))}, nil)
			stream, err := client.ConsumeSession(bg, "q", nil)
			if err != nil {
				t.Fatal(err)
			}
			defer stream.Close()

			_, err = stream.Receive()

			if e := requireCode(t, err, want); e.Message != "nope" {
				t.Fatalf("message = %q", e.Message)
			}
		})
	}
}

func TestASessionLostFrameReturnsSessionLost(t *testing.T) {
	script := newConsumeScript()
	client := newGRPCClient(t, &fakeServer{consume: script.serve(assigned("s1"), sessionLost("renewal failed"))}, nil)
	stream, err := client.ConsumeSession(bg, "q", nil)
	if err != nil {
		t.Fatal(err)
	}
	defer stream.Close()

	_, err = stream.Receive()

	requireCode(t, err, CodeSessionLost)
}

func TestADrainedSessionEndsWithEOF(t *testing.T) {
	client := newGRPCClient(t, &fakeServer{consume: func(stream pb.DaprMQ_ConsumeSessionServer) error {
		if _, err := stream.Recv(); err != nil {
			return err
		}
		_ = stream.Send(assigned("s1"))
		_ = stream.Send(delivered("L1", `1`))
		return stream.Send(drained("s1"))
	}}, nil)
	stream, err := client.ConsumeSession(bg, "q", nil)
	if err != nil {
		t.Fatal(err)
	}
	defer stream.Close()

	if _, err := stream.Receive(); err != nil {
		t.Fatal(err)
	}
	if _, err := stream.Receive(); !errors.Is(err, io.EOF) {
		t.Fatalf("want io.EOF, got %v", err)
	}
	if _, err := stream.Receive(); !errors.Is(err, io.EOF) {
		t.Fatalf("Receive after the end keeps returning io.EOF, got %v", err)
	}
}

func TestCloseHalfClosesSoTheServerSeesEverySettlementFirst(t *testing.T) {
	script := newConsumeScript()
	ended := make(chan struct{})
	serve := script.serve(assigned("s1"), delivered("L1", `1`))
	client := newGRPCClient(t, &fakeServer{consume: func(stream pb.DaprMQ_ConsumeSessionServer) error {
		defer close(ended)
		return serve(stream)
	}}, nil)
	stream, err := client.ConsumeSession(bg, "q", nil)
	if err != nil {
		t.Fatal(err)
	}

	delivery, _ := stream.Receive()
	_ = delivery.Ack()
	if err := stream.Close(); err != nil {
		t.Fatal(err)
	}

	select {
	case <-ended:
	case <-time.After(5 * time.Second):
		t.Fatal("server never saw the half-close")
	}
	if got := receiveSettled(t, script); got.GetAck().GetLockId() != "L1" {
		t.Fatalf("settled = %+v", got)
	}
	if err := stream.Close(); err != nil {
		t.Fatalf("Close is idempotent: %v", err)
	}
	if err := delivery.Ack(); err == nil {
		t.Fatal("settling after Close must fail")
	}
}

func TestCloseCancelsTheCallIfTheServerNeverEndsIt(t *testing.T) {
	defer func(g time.Duration) { closeGrace = g }(closeGrace)
	closeGrace = 100 * time.Millisecond
	cancelled := make(chan struct{})
	client := newGRPCClient(t, &fakeServer{consume: func(stream pb.DaprMQ_ConsumeSessionServer) error {
		_ = stream.Send(assigned("s1"))
		<-stream.Context().Done() // never ends the stream itself, even after the half-close
		close(cancelled)
		return nil
	}}, nil)
	stream, err := client.ConsumeSession(bg, "q", nil)
	if err != nil {
		t.Fatal(err)
	}
	if err := stream.Close(); err != nil {
		t.Fatal(err)
	}

	select {
	case <-cancelled:
	case <-time.After(5 * time.Second):
		t.Fatal("call was never cancelled after the drain")
	}
	if _, err := stream.Receive(); err == nil {
		t.Fatal("Receive must end once the call is cancelled")
	}
}

func TestCancellingTheContextEndsTheStream(t *testing.T) {
	script := newConsumeScript()
	client := newGRPCClient(t, &fakeServer{consume: script.serve(assigned("s1"))}, nil)
	ctx, cancel := context.WithCancel(bg)
	stream, err := client.ConsumeSession(ctx, "q", nil)
	if err != nil {
		t.Fatal(err)
	}
	defer stream.Close()

	go func() {
		time.Sleep(50 * time.Millisecond)
		cancel()
	}()
	_, err = stream.Receive()

	if !errors.Is(err, context.Canceled) {
		t.Fatalf("want context.Canceled, got %v", err)
	}
}

func receiveSettled(t *testing.T, script *consumeScript) *pb.ConsumeSessionRequest {
	t.Helper()
	select {
	case req := <-script.settled:
		return req
	case <-time.After(5 * time.Second):
		t.Fatal("no settlement frame arrived")
		return nil
	}
}
