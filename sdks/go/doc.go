// Package daprmq is a Go client for DaprMQ's HTTP/gRPC API: [Client] for direct queue and session
// operations, and [SessionQueueConsumer], a managed multi-session consume loop built on the
// ConsumeSession streaming RPC.
//
//	client, err := daprmq.NewClient("http://localhost:8002", "localhost:8003", nil)
//	if err != nil {
//		return err
//	}
//	defer client.Close()
//
//	_, err = client.Enqueue(ctx, "orders", []daprmq.EnqueueItem{{Item: order}}, nil)
//
// Every method takes a context first and, where it has options, a nil-able *XxxOptions last.
// Failures are an [*Error] carrying a [Code]; cancellation is the context's own error.
// See docs/CLIENT_SDK.md in this directory for the full guide.
package daprmq
