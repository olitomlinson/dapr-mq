package daprmq

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"sync"
	"time"

	"github.com/olitomlinson/dapr-mq/sdks/go/internal/pb"
)

// closeGrace is how long a closed stream waits for the server to finish the settlements already
// sent and end the stream, before the client tears it down regardless.
const closeGrace = 30 * time.Second

// SessionStream is a managed consume loop for exactly one session, from [Client.ConsumeSession]:
// the server claims a session, streams its items, and renews the lease for as long as the stream
// stays open. No lease id is exposed; settle each [SessionDelivery] through its own methods.
//
// Call Receive from one goroutine. Always Close the stream when done.
type SessionStream struct {
	parent context.Context
	call   pb.DaprMQ_ConsumeSessionClient
	cancel context.CancelFunc

	mu        sync.Mutex // guards sends, closed and sessionID
	closed    bool
	sessionID string
	closeOnce sync.Once
	closeErr  error

	finished error // the terminal result Receive keeps returning
}

// SessionDelivery is one delivered, locked item from a [SessionStream].
type SessionDelivery struct {
	SessionID     string
	LockID        string
	Item          json.RawMessage
	Priority      int
	LockExpiresAt time.Time

	stream *SessionStream
}

// ConsumeSession opens a ConsumeSession stream: it claims options.SessionID, or any available
// session. A failed claim surfaces from the first [SessionStream.Receive] (for example
// CodeNoSessionsAvailable or CodeSessionLocked). Cancelling ctx ends the stream at once,
// releasing the session.
func (c *Client) ConsumeSession(ctx context.Context, queueID string, options *ConsumeSessionOptions) (*SessionStream, error) {
	if options == nil {
		options = &ConsumeSessionOptions{}
	}
	streamCtx, cancel := context.WithCancel(ctx)
	call, err := c.grpc.ConsumeSession(streamCtx)
	if err != nil {
		cancel()
		return nil, err
	}

	start := &pb.ConsumeSessionStart{
		QueueId:                   queueID,
		LeaseSeconds:              int32(wholeSeconds(options.LeaseDuration, 30)),
		PrefetchCount:             int32(max(options.PrefetchCount, 1)),
		SessionIdleTimeoutSeconds: int32(wholeSeconds(options.SessionIdleTimeout, 0)),
	}
	if options.SessionID != "" {
		start.SessionId = &options.SessionID
	}
	s := &SessionStream{parent: ctx, call: call, cancel: cancel, sessionID: options.SessionID}
	if err := call.Send(&pb.ConsumeSessionRequest{Payload: &pb.ConsumeSessionRequest_Start{Start: start}}); err != nil && !errors.Is(err, io.EOF) {
		// io.EOF means the stream already ended; Receive reports why.
		cancel()
		return nil, err
	}
	return s, nil
}

// SessionID is the claimed session's id, once the server has assigned it.
func (s *SessionStream) SessionID() string {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.sessionID
}

// Receive blocks for the next delivery. It returns io.EOF when the stream ends cleanly (for
// example, the session drained after SessionIdleTimeout), a *Error for a failed claim or a lost
// lease, or the context's error after cancellation. Once it returns an error, it keeps returning it.
func (s *SessionStream) Receive() (*SessionDelivery, error) {
	if s.finished != nil {
		return nil, s.finished
	}
	for {
		resp, err := s.call.Recv()
		if err != nil {
			if s.parent.Err() != nil {
				err = s.parent.Err()
			}
			s.cancel() // the stream is over; release it
			return nil, s.finish(err)
		}

		switch p := resp.Payload.(type) {
		case *pb.ConsumeSessionResponse_SessionAssigned:
			s.mu.Lock()
			s.sessionID = p.SessionAssigned.SessionId
			s.mu.Unlock()
		case *pb.ConsumeSessionResponse_Delivered:
			d := p.Delivered
			return &SessionDelivery{
				SessionID: s.SessionID(), LockID: d.LockId, Item: json.RawMessage(d.ItemJson),
				Priority: int(d.Priority), LockExpiresAt: unixSeconds(d.LockExpiresAt), stream: s,
			}, nil
		case *pb.ConsumeSessionResponse_Error:
			return nil, s.finish(&Error{Code: Code(p.Error.ErrorCode), Message: p.Error.Message})
		case *pb.ConsumeSessionResponse_SessionLost:
			return nil, s.finish(&Error{Code: CodeSessionLost, Message: p.SessionLost.Message})
		case *pb.ConsumeSessionResponse_SessionDrained:
			return nil, s.finish(io.EOF)
		}
	}
}

func (s *SessionStream) finish(err error) error {
	s.finished = err
	return err
}

// Close ends the stream gracefully: the server applies every settlement already sent, then
// releases the session. Keep calling Receive until it returns an error to wait for that.
// Calling Close more than once is harmless.
func (s *SessionStream) Close() error {
	s.closeOnce.Do(func() {
		s.mu.Lock()
		s.closed = true
		s.closeErr = s.call.CloseSend()
		s.mu.Unlock()
		time.AfterFunc(closeGrace, s.cancel)
	})
	return s.closeErr
}

func (s *SessionStream) send(req *pb.ConsumeSessionRequest) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.closed {
		return ErrSessionStreamClosed
	}
	err := s.call.Send(req)
	if errors.Is(err, io.EOF) {
		return ErrSessionStreamClosed
	}
	return err
}

// Ack permanently removes the item.
func (d *SessionDelivery) Ack() error {
	return d.stream.send(&pb.ConsumeSessionRequest{Payload: &pb.ConsumeSessionRequest_Ack{Ack: &pb.ConsumeSessionAck{LockId: d.LockID}}})
}

// DeadLetter moves the item to "{queueID}-session-{sessionID}-deadletter".
func (d *SessionDelivery) DeadLetter() error {
	return d.stream.send(&pb.ConsumeSessionRequest{Payload: &pb.ConsumeSessionRequest_DeadLetter{DeadLetter: &pb.ConsumeSessionDeadLetter{LockId: d.LockID}}})
}

// Nack returns the item to its original position in the session for redelivery. It counts
// toward the server's max delivery count, past which the item is dead-lettered.
func (d *SessionDelivery) Nack() error {
	return d.stream.send(&pb.ConsumeSessionRequest{Payload: &pb.ConsumeSessionRequest_Nack{Nack: &pb.ConsumeSessionNack{LockId: d.LockID}}})
}
