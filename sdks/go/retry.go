package daprmq

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math/rand/v2"
	"net"
	"net/http"
	"strconv"
	"syscall"
	"time"
)

const (
	deliveryMarkerHeader = "daprmq-delivery"
	retryTimeoutHeader   = "daprmq-retry-timeout"
	markerNotDelivered   = "not-delivered"
	markerUnknown        = "unknown"
)

func (o RetryOptions) withDefaults() RetryOptions {
	if o.Timeout == 0 {
		o.Timeout = 30 * time.Second
	}
	if o.MinAttemptWindow == 0 {
		o.MinAttemptWindow = 6 * time.Second
	}
	if o.InitialBackoff == 0 {
		o.InitialBackoff = 100 * time.Millisecond
	}
	if o.MaxBackoff == 0 {
		o.MaxBackoff = 2 * time.Second
	}
	return o
}

type request struct {
	operation string
	queueID   string
	path      string
	body      any
	headers   map[string]string
	// unknownIsRetryable: re-sending after an unknown outcome is harmless for this call.
	unknownIsRetryable bool
	idempotencyKeys    []string
}

type response struct {
	status int
	body   []byte
}

func (r *response) ok() bool { return r.status >= 200 && r.status < 300 }

// send makes one REST call under the retry contract (sdks/testing/RETRIES_AND_READINESS.md):
// not-delivered failures are retried within the retry timeout, unknown outcomes only when
// unknownIsRetryable. Every attempt tells the server how much retry time is left; nothing cuts a
// delivered call short except the caller's context and the HTTP client's own timeout. Any other
// response is returned for the caller to map.
func (c *Client) send(ctx context.Context, r request) (*response, error) {
	var payload []byte
	if r.body != nil {
		var err error
		if payload, err = json.Marshal(r.body); err != nil {
			return nil, fmt.Errorf("daprmq: %s: marshal request: %w", r.operation, err)
		}
	}

	retries := c.retry.Timeout > 0
	deadline := time.Now().Add(c.retry.Timeout)
	backoff := c.retry.InitialBackoff

	for {
		req, err := http.NewRequestWithContext(ctx, http.MethodPost, c.baseURL+r.path, bytes.NewReader(payload))
		if err != nil {
			return nil, err
		}
		if payload != nil {
			req.Header.Set("Content-Type", "application/json")
		}
		for k, v := range r.headers {
			req.Header.Set(k, v)
		}
		if retries {
			req.Header.Set(retryTimeoutHeader, strconv.FormatInt(max(1, time.Until(deadline).Milliseconds()), 10))
		}

		var notDelivered bool
		var cause error
		resp, err := c.http.Do(req)
		if err == nil {
			body, readErr := io.ReadAll(resp.Body)
			resp.Body.Close()
			err = readErr
			if err == nil {
				marker := resp.Header.Get(deliveryMarkerHeader)
				if marker != markerNotDelivered && marker != markerUnknown {
					return &response{status: resp.StatusCode, body: body}, nil
				}
				notDelivered = marker == markerNotDelivered
				cause = errors.New(errorMessage(resp.StatusCode, body))
			}
		}
		if err != nil {
			if ctx.Err() != nil {
				return nil, ctx.Err()
			}
			notDelivered, cause = nothingWasSent(err), err
		}

		retryable := notDelivered || r.unknownIsRetryable
		delay := time.Duration(rand.Int64N(int64(backoff) + 1))
		if !retries || !retryable || time.Until(deadline)-delay < c.retry.MinAttemptWindow {
			if notDelivered {
				return nil, &Error{
					Code:      CodeUnavailable,
					Message:   fmt.Sprintf("DaprMQ is unavailable; %s was not performed: %v", r.operation, cause),
					Operation: r.operation, QueueID: r.queueID, cause: cause,
				}
			}
			return nil, &Error{
				Code:      CodeDeliveryUnknown,
				Message:   fmt.Sprintf("the outcome of %s is unknown: it may or may not have been performed (%v)", r.operation, cause),
				Operation: r.operation, QueueID: r.queueID, IdempotencyKeys: r.idempotencyKeys, cause: cause,
			}
		}

		if err := sleep(ctx, delay); err != nil {
			return nil, err
		}
		backoff = min(backoff*2, c.retry.MaxBackoff)
	}
}

// nothingWasSent: the connection was refused or the host didn't resolve, so the request never
// left. Anything else after the request was handed over (a reset, a timeout) is an unknown outcome.
func nothingWasSent(err error) bool {
	if errors.Is(err, syscall.ECONNREFUSED) {
		return true
	}
	var dnsErr *net.DNSError
	if errors.As(err, &dnsErr) {
		return true
	}
	var opErr *net.OpError
	return errors.As(err, &opErr) && opErr.Op == "dial"
}

func sleep(ctx context.Context, d time.Duration) error {
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-t.C:
		return nil
	}
}

type errorBody struct {
	Message   string `json:"message"`
	ErrorCode string `json:"errorCode"`
}

func parseErrorBody(body []byte) errorBody {
	var b errorBody
	_ = json.Unmarshal(body, &b)
	return b
}

func errorMessage(status int, body []byte) string {
	if m := parseErrorBody(body).Message; m != "" {
		return m
	}
	return fmt.Sprintf("request failed with status %d", status)
}
