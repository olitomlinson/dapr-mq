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

// QueueStream is a managed consume loop for a plain queue, from [Client.Consume]: the server keeps
// up to PrefetchCount locked items delivered, refills as they are settled, and renews the lock of
// every delivered item until it is settled, so the client never extends locks. When the stream
// ends, the server returns every unsettled item to its position straight away.
//
// Call Receive from one goroutine. Always Close the stream when done.
type QueueStream struct {
	parent         context.Context
	call           pb.DaprMQ_ConsumeClient
	cancel         context.CancelFunc
	onSettleFailed func(lockID string, err error)

	mu        sync.Mutex // guards sends and closed
	closed    bool
	closeOnce sync.Once
	closeErr  error

	finished error // the terminal result Receive keeps returning
}

// QueueDelivery is one delivered, locked item from a [QueueStream].
type QueueDelivery struct {
	LockID        string
	Item          json.RawMessage
	Priority      int
	LockExpiresAt time.Time
	// DeliveryCount counts this delivery: 1 is the first, 2 the first redelivery after a Nack or
	// a lapsed lock, and so on.
	DeliveryCount int

	stream *QueueStream
}

// Consume opens a Consume stream on a plain queue. Cancelling ctx ends the stream at once.
func (c *Client) Consume(ctx context.Context, queueID string, options *ConsumeOptions) (*QueueStream, error) {
	if options == nil {
		options = &ConsumeOptions{}
	}
	streamCtx, cancel := context.WithCancel(ctx)
	call, err := c.grpc.Consume(streamCtx)
	if err != nil {
		cancel()
		return nil, err
	}

	start := &pb.ConsumeStart{
		QueueId:                 queueID,
		PrefetchCount:           int32(max(options.PrefetchCount, 1)),
		LockTtlSeconds:          int32(wholeSeconds(options.LockTTL, 30)),
		AllowCompetingConsumers: options.AllowCompetingConsumers,
	}
	s := &QueueStream{parent: ctx, call: call, cancel: cancel, onSettleFailed: options.OnSettleFailed}
	if err := call.Send(&pb.ConsumeRequest{Payload: &pb.ConsumeRequest_Start{Start: start}}); err != nil && !errors.Is(err, io.EOF) {
		// io.EOF means the stream already ended; Receive reports why.
		cancel()
		return nil, err
	}
	return s, nil
}

// Receive blocks for the next delivery. It returns io.EOF when the server ends the stream cleanly
// (after Close), a *Error for a terminal error frame, or the context's error after cancellation.
// Once it returns an error, it keeps returning it.
func (s *QueueStream) Receive() (*QueueDelivery, error) {
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
		case *pb.ConsumeResponse_Delivered:
			d := p.Delivered
			return &QueueDelivery{
				LockID: d.LockId, Item: json.RawMessage(d.ItemJson), Priority: int(d.Priority),
				LockExpiresAt: unixSeconds(d.LockExpiresAt), DeliveryCount: int(d.DeliveryCount), stream: s,
			}, nil
		case *pb.ConsumeResponse_SettleFailed:
			if s.onSettleFailed != nil {
				f := p.SettleFailed
				s.onSettleFailed(f.LockId, lockError(f.ErrorCode, f.Message, 0))
			}
		case *pb.ConsumeResponse_Error:
			return nil, s.finish(&Error{Code: Code(p.Error.ErrorCode), Message: p.Error.Message})
		}
	}
}

func (s *QueueStream) finish(err error) error {
	s.finished = err
	return err
}

// Close ends the stream gracefully: the server applies every settlement already sent, then
// returns the unsettled items to the queue. Keep calling Receive until it returns an error to
// wait for that. Calling Close more than once is harmless.
func (s *QueueStream) Close() error {
	s.closeOnce.Do(func() {
		s.mu.Lock()
		s.closed = true
		s.closeErr = s.call.CloseSend()
		s.mu.Unlock()
		time.AfterFunc(closeGrace, s.cancel)
	})
	return s.closeErr
}

func (s *QueueStream) send(req *pb.ConsumeRequest) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.closed {
		return ErrStreamClosed
	}
	err := s.call.Send(req)
	if errors.Is(err, io.EOF) {
		return ErrStreamClosed
	}
	return err
}

// Ack permanently removes the item. A rejection arrives later through
// [ConsumeOptions.OnSettleFailed]; the error here only covers sending the frame.
func (d *QueueDelivery) Ack() error {
	return d.stream.send(&pb.ConsumeRequest{Payload: &pb.ConsumeRequest_Ack{Ack: &pb.ConsumeAck{LockId: d.LockID}}})
}

// Nack returns the item to its original position for redelivery. It counts toward the server's
// max delivery count, past which the item is dead-lettered.
func (d *QueueDelivery) Nack() error {
	return d.stream.send(&pb.ConsumeRequest{Payload: &pb.ConsumeRequest_Nack{Nack: &pb.ConsumeNack{LockId: d.LockID}}})
}

// DeadLetter moves the item to "{queueID}-deadletter".
func (d *QueueDelivery) DeadLetter() error {
	return d.stream.send(&pb.ConsumeRequest{Payload: &pb.ConsumeRequest_DeadLetter{DeadLetter: &pb.ConsumeDeadLetter{LockId: d.LockID}}})
}
