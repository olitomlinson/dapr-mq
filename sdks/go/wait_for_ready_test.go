package daprmq

import (
	"context"
	"errors"
	"sync/atomic"
	"testing"
	"time"

	"google.golang.org/grpc/codes"
	healthpb "google.golang.org/grpc/health/grpc_health_v1"
	"google.golang.org/grpc/status"
)

func watchSequence(watched chan<- string, steps ...func(healthpb.Health_WatchServer) error) *fakeHealth {
	var call atomic.Int32
	return &fakeHealth{watch: func(req *healthpb.HealthCheckRequest, stream healthpb.Health_WatchServer) error {
		if watched != nil {
			watched <- req.Service
		}
		n := int(call.Add(1))
		return steps[min(n, len(steps))-1](stream)
	}}
}

func sendStatuses(statuses ...healthpb.HealthCheckResponse_ServingStatus) func(healthpb.Health_WatchServer) error {
	return func(stream healthpb.Health_WatchServer) error {
		for _, s := range statuses {
			if err := stream.Send(&healthpb.HealthCheckResponse{Status: s}); err != nil {
				return err
			}
		}
		<-stream.Context().Done()
		return nil
	}
}

func failWith(code codes.Code) func(healthpb.Health_WatchServer) error {
	return func(healthpb.Health_WatchServer) error { return status.Error(code, "") }
}

func endStream(healthpb.Health_WatchServer) error { return nil }

func timeout(t *testing.T, d time.Duration) context.Context {
	ctx, cancel := context.WithTimeout(context.Background(), d)
	t.Cleanup(cancel)
	return ctx
}

func TestNotServingThenServingReturnsAndWatchesTheOperationsService(t *testing.T) {
	watched := make(chan string, 4)
	client := newGRPCClient(t, nil, watchSequence(watched, sendStatuses(healthpb.HealthCheckResponse_NOT_SERVING, healthpb.HealthCheckResponse_SERVING)))

	if err := client.WaitForReady(timeout(t, 5*time.Second), nil); err != nil {
		t.Fatal(err)
	}

	if s := <-watched; s != "daprmq.DaprMQ.operations" {
		t.Fatalf("watched %q", s)
	}
}

func TestAnotherServiceCanBeWatched(t *testing.T) {
	watched := make(chan string, 4)
	client := newGRPCClient(t, nil, watchSequence(watched, sendStatuses(healthpb.HealthCheckResponse_SERVING)))

	if err := client.WaitForReady(timeout(t, 5*time.Second), &WaitForReadyOptions{Service: "daprmq.DaprMQ"}); err != nil {
		t.Fatal(err)
	}

	if s := <-watched; s != "daprmq.DaprMQ" {
		t.Fatalf("watched %q", s)
	}
}

func TestUnavailableThenServingReconnects(t *testing.T) {
	client := newGRPCClient(t, nil, watchSequence(nil, failWith(codes.Unavailable), sendStatuses(healthpb.HealthCheckResponse_SERVING)))

	if err := client.WaitForReady(timeout(t, 5*time.Second), nil); err != nil {
		t.Fatal(err)
	}
}

func TestStreamEndingBeforeServingReconnects(t *testing.T) {
	client := newGRPCClient(t, nil, watchSequence(nil, endStream, sendStatuses(healthpb.HealthCheckResponse_SERVING)))

	if err := client.WaitForReady(timeout(t, 5*time.Second), nil); err != nil {
		t.Fatal(err)
	}
}

func TestUnimplementedReturnsErrUnsupported(t *testing.T) {
	client := newGRPCClient(t, nil, watchSequence(nil, failWith(codes.Unimplemented)))

	err := client.WaitForReady(timeout(t, 5*time.Second), nil)

	if !errors.Is(err, errors.ErrUnsupported) {
		t.Fatalf("want errors.ErrUnsupported, got %v", err)
	}
}

func TestNeverServingIsBoundedByTheCaller(t *testing.T) {
	client := newGRPCClient(t, nil, watchSequence(nil, sendStatuses(healthpb.HealthCheckResponse_NOT_SERVING)))

	err := client.WaitForReady(timeout(t, 200*time.Millisecond), nil)

	if !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("want context.DeadlineExceeded, got %v", err)
	}
}
