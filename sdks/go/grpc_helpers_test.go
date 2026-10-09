package daprmq

import (
	"context"
	"net"
	"net/http"
	"testing"

	"github.com/olitomlinson/dapr-mq/sdks/go/internal/pb"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
	healthpb "google.golang.org/grpc/health/grpc_health_v1"
	"google.golang.org/grpc/test/bufconn"
)

// fakeServer is an in-process DaprMQ gRPC server: each test scripts ConsumeSession (and, through
// health, the health service) to exercise the real client end to end over a real gRPC stream.
type fakeServer struct {
	pb.UnimplementedDaprMQServer
	consume      func(stream pb.DaprMQ_ConsumeSessionServer) error
	consumeQueue func(stream pb.DaprMQ_ConsumeServer) error
}

func (f *fakeServer) ConsumeSession(stream pb.DaprMQ_ConsumeSessionServer) error {
	return f.consume(stream)
}

func (f *fakeServer) Consume(stream pb.DaprMQ_ConsumeServer) error {
	return f.consumeQueue(stream)
}

type fakeHealth struct {
	healthpb.UnimplementedHealthServer
	watch func(req *healthpb.HealthCheckRequest, stream healthpb.Health_WatchServer) error
}

func (f *fakeHealth) Watch(req *healthpb.HealthCheckRequest, stream healthpb.Health_WatchServer) error {
	return f.watch(req, stream)
}

// newGRPCClient starts the given services on an in-memory listener and returns a Client wired to
// it. Unary calls go to transport (nil = a transport that fails the test).
func newGRPCClient(t *testing.T, daprmq pb.DaprMQServer, health healthpb.HealthServer) *Client {
	t.Helper()
	listener := bufconn.Listen(1 << 20)
	server := grpc.NewServer()
	if daprmq != nil {
		pb.RegisterDaprMQServer(server, daprmq)
	}
	if health != nil {
		healthpb.RegisterHealthServer(server, health)
	}
	go func() { _ = server.Serve(listener) }()
	t.Cleanup(server.Stop)

	c, err := NewClient("http://localhost:5000", "passthrough:///bufnet", &ClientOptions{
		HTTPClient: &http.Client{Transport: newSequence(respond(599, nil, nil))},
		GRPCDialOptions: []grpc.DialOption{
			grpc.WithContextDialer(func(ctx context.Context, _ string) (net.Conn, error) { return listener.DialContext(ctx) }),
			grpc.WithTransportCredentials(insecure.NewCredentials()),
		},
		Retry: fastRetry,
	})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = c.Close() })
	return c
}

// consumeFrames scripts a ConsumeSession server: it checks the Start frame, sends the given
// frames, then records every settlement frame the client sends until the client half-closes.
type consumeScript struct {
	start   chan *pb.ConsumeSessionStart
	settled chan *pb.ConsumeSessionRequest
}

func newConsumeScript() *consumeScript {
	return &consumeScript{start: make(chan *pb.ConsumeSessionStart, 16), settled: make(chan *pb.ConsumeSessionRequest, 64)}
}

func (s *consumeScript) serve(frames ...*pb.ConsumeSessionResponse) func(pb.DaprMQ_ConsumeSessionServer) error {
	return func(stream pb.DaprMQ_ConsumeSessionServer) error {
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
				return nil // client half-closed or went away
			}
			s.settled <- req
		}
	}
}

func assigned(sessionID string) *pb.ConsumeSessionResponse {
	return &pb.ConsumeSessionResponse{Payload: &pb.ConsumeSessionResponse_SessionAssigned{
		SessionAssigned: &pb.SessionAssigned{SessionId: sessionID, LeaseExpiresAt: 1700000000},
	}}
}

func delivered(lockID, itemJSON string) *pb.ConsumeSessionResponse {
	return &pb.ConsumeSessionResponse{Payload: &pb.ConsumeSessionResponse_Delivered{
		Delivered: &pb.SessionDelivered{LockId: lockID, ItemJson: itemJSON, Priority: 1, LockExpiresAt: 1700000030},
	}}
}

func sessionError(code, message string) *pb.ConsumeSessionResponse {
	return &pb.ConsumeSessionResponse{Payload: &pb.ConsumeSessionResponse_Error{
		Error: &pb.SessionError{ErrorCode: code, Message: message},
	}}
}

func sessionLost(message string) *pb.ConsumeSessionResponse {
	return &pb.ConsumeSessionResponse{Payload: &pb.ConsumeSessionResponse_SessionLost{
		SessionLost: &pb.SessionLost{Message: message},
	}}
}

func drained(sessionID string) *pb.ConsumeSessionResponse {
	return &pb.ConsumeSessionResponse{Payload: &pb.ConsumeSessionResponse_SessionDrained{
		SessionDrained: &pb.SessionDrained{SessionId: sessionID},
	}}
}
