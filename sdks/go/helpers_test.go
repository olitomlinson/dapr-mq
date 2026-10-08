package daprmq

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"strings"
	"sync"
	"testing"
	"time"
)

// fastRetry keeps retry tests quick: tiny backoff, and a minimum attempt window small enough
// that every attempt in the timeout is made.
var fastRetry = RetryOptions{
	Timeout:          5 * time.Second,
	MinAttemptWindow: 10 * time.Millisecond,
	InitialBackoff:   time.Millisecond,
	MaxBackoff:       5 * time.Millisecond,
}

type answer func(req *http.Request) (*http.Response, error)

// sequenceTransport answers each attempt in turn; the last answer repeats.
type sequenceTransport struct {
	mu       sync.Mutex
	answers  []answer
	requests []*http.Request
	bodies   []string
}

func newSequence(answers ...answer) *sequenceTransport {
	return &sequenceTransport{answers: answers}
}

func (t *sequenceTransport) RoundTrip(req *http.Request) (*http.Response, error) {
	var body []byte
	if req.Body != nil {
		body, _ = io.ReadAll(req.Body)
	}
	t.mu.Lock()
	t.requests = append(t.requests, req)
	t.bodies = append(t.bodies, string(body))
	a := t.answers[min(len(t.requests), len(t.answers))-1]
	t.mu.Unlock()
	return a(req)
}

func (t *sequenceTransport) count() int {
	t.mu.Lock()
	defer t.mu.Unlock()
	return len(t.requests)
}

func respond(status int, body any, headers map[string]string) answer {
	return func(req *http.Request) (*http.Response, error) {
		var b []byte
		if body != nil {
			b, _ = json.Marshal(body)
		}
		h := http.Header{}
		for k, v := range headers {
			h.Set(k, v)
		}
		return &http.Response{StatusCode: status, Header: h, Body: io.NopCloser(bytes.NewReader(b)), Request: req}, nil
	}
}

func ok(body any) answer { return respond(200, body, nil) }

var notDelivered = respond(503,
	map[string]any{"message": "unavailable", "success": false, "errorCode": "UNAVAILABLE"},
	map[string]string{"daprmq-delivery": "not-delivered"})

var unknown = respond(504,
	map[string]any{"message": "unknown", "success": false, "errorCode": "DELIVERY_UNKNOWN"},
	map[string]string{"daprmq-delivery": "unknown"})

var okEnqueue = ok(map[string]any{"success": true, "message": "ok", "itemsEnqueued": 1, "itemsDeduplicated": 0})

func newTestClient(t *testing.T, transport http.RoundTripper, retry RetryOptions) *Client {
	t.Helper()
	c, err := NewClient("http://localhost:5000/", "localhost:5001", &ClientOptions{
		HTTPClient: &http.Client{Transport: transport},
		Retry:      retry,
	})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = c.Close() })
	return c
}

func requireCode(t *testing.T, err error, code Code) *Error {
	t.Helper()
	var e *Error
	if !errors.As(err, &e) {
		t.Fatalf("want *daprmq.Error with code %s, got %v", code, err)
	}
	if e.Code != code {
		t.Fatalf("want code %s, got %s (%v)", code, e.Code, err)
	}
	return e
}

func contains(s, sub string) bool { return strings.Contains(s, sub) }
