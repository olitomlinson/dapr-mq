package daprmq

import (
	"errors"
	"fmt"
)

// Code identifies what went wrong in an [*Error]. Check it with [errors.As]:
//
//	var mqErr *daprmq.Error
//	if errors.As(err, &mqErr) && mqErr.Code == daprmq.CodeLockNotFound {
//		// already settled, or expired
//	}
//
// Codes the server sends that this SDK doesn't name come through unchanged.
type Code string

const (
	CodeLockNotFound            Code = "LOCK_NOT_FOUND"
	CodeLockExpired             Code = "LOCK_EXPIRED"
	CodeValidation              Code = "VALIDATION_ERROR"
	CodeNotFound                Code = "NOT_FOUND"
	CodeSessionNotFound         Code = "SESSION_NOT_FOUND"
	CodeSessionLocked           Code = "SESSION_LOCKED"
	CodeSessionLeaseExpired     Code = "SESSION_LEASE_EXPIRED"
	CodeInvalidLeaseID          Code = "INVALID_LEASE_ID"
	CodeSessionActorUnavailable Code = "SESSION_ACTOR_UNAVAILABLE"
	CodeNoSessionsAvailable     Code = "NO_SESSIONS_AVAILABLE"
	// CodeSessionLost: a session was claimed but its lease could not be maintained afterwards
	// (the server sent a terminal SessionLost frame), as distinct from a claim that never succeeded.
	CodeSessionLost Code = "SESSION_LOST"
	// CodeUnavailable: the operation was certainly not performed (DaprMQ couldn't serve it) and
	// retrying ran out of [RetryOptions.Timeout]. Always safe to repeat later.
	CodeUnavailable Code = "UNAVAILABLE"
	// CodeDeliveryUnknown: the operation may or may not have been performed (for example, the
	// connection broke after it was sent) and it isn't safe to repeat automatically. See
	// docs/TIMEOUTS_AND_RETRIES.md for what to do per operation.
	CodeDeliveryUnknown Code = "DELIVERY_UNKNOWN"
)

// Error is the error every DaprMQ failure returns. Caller cancellation is not one: it comes back
// as the context's own error (context.Canceled or context.DeadlineExceeded).
type Error struct {
	Code    Code
	Message string
	// StatusCode is the HTTP status of the response, or 0 when there was none.
	StatusCode int
	// Operation and QueueID name the call, for CodeUnavailable and CodeDeliveryUnknown.
	Operation string
	QueueID   string
	// IdempotencyKeys holds, for an Enqueue that failed with CodeDeliveryUnknown, each item's key
	// in order ("" where the item had none).
	IdempotencyKeys []string

	cause error
}

func (e *Error) Error() string {
	if e.Code == "" {
		return "daprmq: " + e.Message
	}
	return fmt.Sprintf("daprmq: %s: %s", e.Code, e.Message)
}

func (e *Error) Unwrap() error { return e.cause }

// ErrSessionStreamClosed is returned when settling a delivery after its [SessionStream] was closed.
var ErrSessionStreamClosed = errors.New("daprmq: session stream is closed")

func lockError(code, message string, status int) *Error {
	switch code {
	case "INVALID_LOCK_ID", "INVALID_TTL", "VALIDATION_ERROR":
		return &Error{Code: CodeValidation, Message: message, StatusCode: status}
	}
	return &Error{Code: Code(code), Message: message, StatusCode: status}
}
