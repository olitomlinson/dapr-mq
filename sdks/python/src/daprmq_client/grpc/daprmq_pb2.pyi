from google.protobuf.internal import containers as _containers
from google.protobuf import descriptor as _descriptor
from google.protobuf import message as _message
from collections.abc import Iterable as _Iterable, Mapping as _Mapping
from typing import ClassVar as _ClassVar, Optional as _Optional, Union as _Union

DESCRIPTOR: _descriptor.FileDescriptor

class EnqueueItem(_message.Message):
    __slots__ = ("item_json", "priority", "idempotency_key", "session_id")
    ITEM_JSON_FIELD_NUMBER: _ClassVar[int]
    PRIORITY_FIELD_NUMBER: _ClassVar[int]
    IDEMPOTENCY_KEY_FIELD_NUMBER: _ClassVar[int]
    SESSION_ID_FIELD_NUMBER: _ClassVar[int]
    item_json: str
    priority: int
    idempotency_key: str
    session_id: str
    def __init__(self, item_json: _Optional[str] = ..., priority: _Optional[int] = ..., idempotency_key: _Optional[str] = ..., session_id: _Optional[str] = ...) -> None: ...

class EnqueueRequest(_message.Message):
    __slots__ = ("queue_id", "items")
    QUEUE_ID_FIELD_NUMBER: _ClassVar[int]
    ITEMS_FIELD_NUMBER: _ClassVar[int]
    queue_id: str
    items: _containers.RepeatedCompositeFieldContainer[EnqueueItem]
    def __init__(self, queue_id: _Optional[str] = ..., items: _Optional[_Iterable[_Union[EnqueueItem, _Mapping]]] = ...) -> None: ...

class EnqueueResponse(_message.Message):
    __slots__ = ("success", "message", "items_enqueued", "items_deduplicated")
    SUCCESS_FIELD_NUMBER: _ClassVar[int]
    MESSAGE_FIELD_NUMBER: _ClassVar[int]
    ITEMS_ENQUEUED_FIELD_NUMBER: _ClassVar[int]
    ITEMS_DEDUPLICATED_FIELD_NUMBER: _ClassVar[int]
    success: bool
    message: str
    items_enqueued: int
    items_deduplicated: int
    def __init__(self, success: _Optional[bool] = ..., message: _Optional[str] = ..., items_enqueued: _Optional[int] = ..., items_deduplicated: _Optional[int] = ...) -> None: ...

class DequeueRequest(_message.Message):
    __slots__ = ("queue_id", "count", "lease_id")
    QUEUE_ID_FIELD_NUMBER: _ClassVar[int]
    COUNT_FIELD_NUMBER: _ClassVar[int]
    LEASE_ID_FIELD_NUMBER: _ClassVar[int]
    queue_id: str
    count: int
    lease_id: str
    def __init__(self, queue_id: _Optional[str] = ..., count: _Optional[int] = ..., lease_id: _Optional[str] = ...) -> None: ...

class DequeueResponse(_message.Message):
    __slots__ = ("success", "empty", "locked")
    SUCCESS_FIELD_NUMBER: _ClassVar[int]
    EMPTY_FIELD_NUMBER: _ClassVar[int]
    LOCKED_FIELD_NUMBER: _ClassVar[int]
    success: DequeueSuccess
    empty: DequeueEmpty
    locked: DequeueBlocked
    def __init__(self, success: _Optional[_Union[DequeueSuccess, _Mapping]] = ..., empty: _Optional[_Union[DequeueEmpty, _Mapping]] = ..., locked: _Optional[_Union[DequeueBlocked, _Mapping]] = ...) -> None: ...

class DequeueSuccess(_message.Message):
    __slots__ = ("item_json", "priority")
    ITEM_JSON_FIELD_NUMBER: _ClassVar[int]
    PRIORITY_FIELD_NUMBER: _ClassVar[int]
    item_json: _containers.RepeatedScalarFieldContainer[str]
    priority: _containers.RepeatedScalarFieldContainer[int]
    def __init__(self, item_json: _Optional[_Iterable[str]] = ..., priority: _Optional[_Iterable[int]] = ...) -> None: ...

class DequeueEmpty(_message.Message):
    __slots__ = ("message",)
    MESSAGE_FIELD_NUMBER: _ClassVar[int]
    message: str
    def __init__(self, message: _Optional[str] = ...) -> None: ...

class DequeueBlocked(_message.Message):
    __slots__ = ("message", "lock_expires_at")
    MESSAGE_FIELD_NUMBER: _ClassVar[int]
    LOCK_EXPIRES_AT_FIELD_NUMBER: _ClassVar[int]
    message: str
    lock_expires_at: float
    def __init__(self, message: _Optional[str] = ..., lock_expires_at: _Optional[float] = ...) -> None: ...

class DequeueLockedRequest(_message.Message):
    __slots__ = ("queue_id", "ttl_seconds", "allow_competing_consumers", "count", "lease_id")
    QUEUE_ID_FIELD_NUMBER: _ClassVar[int]
    TTL_SECONDS_FIELD_NUMBER: _ClassVar[int]
    ALLOW_COMPETING_CONSUMERS_FIELD_NUMBER: _ClassVar[int]
    COUNT_FIELD_NUMBER: _ClassVar[int]
    LEASE_ID_FIELD_NUMBER: _ClassVar[int]
    queue_id: str
    ttl_seconds: int
    allow_competing_consumers: bool
    count: int
    lease_id: str
    def __init__(self, queue_id: _Optional[str] = ..., ttl_seconds: _Optional[int] = ..., allow_competing_consumers: _Optional[bool] = ..., count: _Optional[int] = ..., lease_id: _Optional[str] = ...) -> None: ...

class DequeueLockedResponse(_message.Message):
    __slots__ = ("success", "empty", "locked")
    SUCCESS_FIELD_NUMBER: _ClassVar[int]
    EMPTY_FIELD_NUMBER: _ClassVar[int]
    LOCKED_FIELD_NUMBER: _ClassVar[int]
    success: DequeueLockedSuccess
    empty: DequeueEmpty
    locked: DequeueBlocked
    def __init__(self, success: _Optional[_Union[DequeueLockedSuccess, _Mapping]] = ..., empty: _Optional[_Union[DequeueEmpty, _Mapping]] = ..., locked: _Optional[_Union[DequeueBlocked, _Mapping]] = ...) -> None: ...

class DequeueLockedSuccess(_message.Message):
    __slots__ = ("item_json", "priority", "lock_id", "lock_expires_at")
    ITEM_JSON_FIELD_NUMBER: _ClassVar[int]
    PRIORITY_FIELD_NUMBER: _ClassVar[int]
    LOCK_ID_FIELD_NUMBER: _ClassVar[int]
    LOCK_EXPIRES_AT_FIELD_NUMBER: _ClassVar[int]
    item_json: _containers.RepeatedScalarFieldContainer[str]
    priority: _containers.RepeatedScalarFieldContainer[int]
    lock_id: _containers.RepeatedScalarFieldContainer[str]
    lock_expires_at: _containers.RepeatedScalarFieldContainer[float]
    def __init__(self, item_json: _Optional[_Iterable[str]] = ..., priority: _Optional[_Iterable[int]] = ..., lock_id: _Optional[_Iterable[str]] = ..., lock_expires_at: _Optional[_Iterable[float]] = ...) -> None: ...

class AcknowledgeRequest(_message.Message):
    __slots__ = ("queue_id", "lock_id", "lease_id")
    QUEUE_ID_FIELD_NUMBER: _ClassVar[int]
    LOCK_ID_FIELD_NUMBER: _ClassVar[int]
    LEASE_ID_FIELD_NUMBER: _ClassVar[int]
    queue_id: str
    lock_id: str
    lease_id: str
    def __init__(self, queue_id: _Optional[str] = ..., lock_id: _Optional[str] = ..., lease_id: _Optional[str] = ...) -> None: ...

class AcknowledgeResponse(_message.Message):
    __slots__ = ("success", "message", "items_acknowledged", "error_code")
    SUCCESS_FIELD_NUMBER: _ClassVar[int]
    MESSAGE_FIELD_NUMBER: _ClassVar[int]
    ITEMS_ACKNOWLEDGED_FIELD_NUMBER: _ClassVar[int]
    ERROR_CODE_FIELD_NUMBER: _ClassVar[int]
    success: bool
    message: str
    items_acknowledged: int
    error_code: str
    def __init__(self, success: _Optional[bool] = ..., message: _Optional[str] = ..., items_acknowledged: _Optional[int] = ..., error_code: _Optional[str] = ...) -> None: ...

class ExtendLockRequest(_message.Message):
    __slots__ = ("queue_id", "lock_id", "additional_ttl_seconds", "lease_id")
    QUEUE_ID_FIELD_NUMBER: _ClassVar[int]
    LOCK_ID_FIELD_NUMBER: _ClassVar[int]
    ADDITIONAL_TTL_SECONDS_FIELD_NUMBER: _ClassVar[int]
    LEASE_ID_FIELD_NUMBER: _ClassVar[int]
    queue_id: str
    lock_id: str
    additional_ttl_seconds: int
    lease_id: str
    def __init__(self, queue_id: _Optional[str] = ..., lock_id: _Optional[str] = ..., additional_ttl_seconds: _Optional[int] = ..., lease_id: _Optional[str] = ...) -> None: ...

class ExtendLockResponse(_message.Message):
    __slots__ = ("success", "new_expires_at", "error_code", "error_message")
    SUCCESS_FIELD_NUMBER: _ClassVar[int]
    NEW_EXPIRES_AT_FIELD_NUMBER: _ClassVar[int]
    ERROR_CODE_FIELD_NUMBER: _ClassVar[int]
    ERROR_MESSAGE_FIELD_NUMBER: _ClassVar[int]
    success: bool
    new_expires_at: float
    error_code: str
    error_message: str
    def __init__(self, success: _Optional[bool] = ..., new_expires_at: _Optional[float] = ..., error_code: _Optional[str] = ..., error_message: _Optional[str] = ...) -> None: ...

class DeadLetterRequest(_message.Message):
    __slots__ = ("queue_id", "lock_id", "lease_id")
    QUEUE_ID_FIELD_NUMBER: _ClassVar[int]
    LOCK_ID_FIELD_NUMBER: _ClassVar[int]
    LEASE_ID_FIELD_NUMBER: _ClassVar[int]
    queue_id: str
    lock_id: str
    lease_id: str
    def __init__(self, queue_id: _Optional[str] = ..., lock_id: _Optional[str] = ..., lease_id: _Optional[str] = ...) -> None: ...

class DeadLetterResponse(_message.Message):
    __slots__ = ("success", "error")
    SUCCESS_FIELD_NUMBER: _ClassVar[int]
    ERROR_FIELD_NUMBER: _ClassVar[int]
    success: DeadLetterSuccess
    error: DeadLetterError
    def __init__(self, success: _Optional[_Union[DeadLetterSuccess, _Mapping]] = ..., error: _Optional[_Union[DeadLetterError, _Mapping]] = ...) -> None: ...

class DeadLetterSuccess(_message.Message):
    __slots__ = ("dlq_id",)
    DLQ_ID_FIELD_NUMBER: _ClassVar[int]
    dlq_id: str
    def __init__(self, dlq_id: _Optional[str] = ...) -> None: ...

class DeadLetterError(_message.Message):
    __slots__ = ("error_code", "message")
    ERROR_CODE_FIELD_NUMBER: _ClassVar[int]
    MESSAGE_FIELD_NUMBER: _ClassVar[int]
    error_code: str
    message: str
    def __init__(self, error_code: _Optional[str] = ..., message: _Optional[str] = ...) -> None: ...

class AcceptSessionRequest(_message.Message):
    __slots__ = ("queue_id", "session_id", "lease_seconds")
    QUEUE_ID_FIELD_NUMBER: _ClassVar[int]
    SESSION_ID_FIELD_NUMBER: _ClassVar[int]
    LEASE_SECONDS_FIELD_NUMBER: _ClassVar[int]
    queue_id: str
    session_id: str
    lease_seconds: int
    def __init__(self, queue_id: _Optional[str] = ..., session_id: _Optional[str] = ..., lease_seconds: _Optional[int] = ...) -> None: ...

class AcceptSessionResponse(_message.Message):
    __slots__ = ("session_id", "lease_id", "lease_expires_at")
    SESSION_ID_FIELD_NUMBER: _ClassVar[int]
    LEASE_ID_FIELD_NUMBER: _ClassVar[int]
    LEASE_EXPIRES_AT_FIELD_NUMBER: _ClassVar[int]
    session_id: str
    lease_id: str
    lease_expires_at: float
    def __init__(self, session_id: _Optional[str] = ..., lease_id: _Optional[str] = ..., lease_expires_at: _Optional[float] = ...) -> None: ...

class RenewSessionLeaseRequest(_message.Message):
    __slots__ = ("queue_id", "session_id", "lease_id", "additional_seconds")
    QUEUE_ID_FIELD_NUMBER: _ClassVar[int]
    SESSION_ID_FIELD_NUMBER: _ClassVar[int]
    LEASE_ID_FIELD_NUMBER: _ClassVar[int]
    ADDITIONAL_SECONDS_FIELD_NUMBER: _ClassVar[int]
    queue_id: str
    session_id: str
    lease_id: str
    additional_seconds: int
    def __init__(self, queue_id: _Optional[str] = ..., session_id: _Optional[str] = ..., lease_id: _Optional[str] = ..., additional_seconds: _Optional[int] = ...) -> None: ...

class RenewSessionLeaseResponse(_message.Message):
    __slots__ = ("new_expires_at",)
    NEW_EXPIRES_AT_FIELD_NUMBER: _ClassVar[int]
    new_expires_at: float
    def __init__(self, new_expires_at: _Optional[float] = ...) -> None: ...

class ReleaseSessionRequest(_message.Message):
    __slots__ = ("queue_id", "session_id", "lease_id")
    QUEUE_ID_FIELD_NUMBER: _ClassVar[int]
    SESSION_ID_FIELD_NUMBER: _ClassVar[int]
    LEASE_ID_FIELD_NUMBER: _ClassVar[int]
    queue_id: str
    session_id: str
    lease_id: str
    def __init__(self, queue_id: _Optional[str] = ..., session_id: _Optional[str] = ..., lease_id: _Optional[str] = ...) -> None: ...

class ReleaseSessionResponse(_message.Message):
    __slots__ = ("success",)
    SUCCESS_FIELD_NUMBER: _ClassVar[int]
    success: bool
    def __init__(self, success: _Optional[bool] = ...) -> None: ...

class ConsumeSessionRequest(_message.Message):
    __slots__ = ("start", "ack", "dead_letter")
    START_FIELD_NUMBER: _ClassVar[int]
    ACK_FIELD_NUMBER: _ClassVar[int]
    DEAD_LETTER_FIELD_NUMBER: _ClassVar[int]
    start: ConsumeSessionStart
    ack: ConsumeSessionAck
    dead_letter: ConsumeSessionDeadLetter
    def __init__(self, start: _Optional[_Union[ConsumeSessionStart, _Mapping]] = ..., ack: _Optional[_Union[ConsumeSessionAck, _Mapping]] = ..., dead_letter: _Optional[_Union[ConsumeSessionDeadLetter, _Mapping]] = ...) -> None: ...

class ConsumeSessionStart(_message.Message):
    __slots__ = ("queue_id", "session_id", "lease_seconds", "prefetch_count")
    QUEUE_ID_FIELD_NUMBER: _ClassVar[int]
    SESSION_ID_FIELD_NUMBER: _ClassVar[int]
    LEASE_SECONDS_FIELD_NUMBER: _ClassVar[int]
    PREFETCH_COUNT_FIELD_NUMBER: _ClassVar[int]
    queue_id: str
    session_id: str
    lease_seconds: int
    prefetch_count: int
    def __init__(self, queue_id: _Optional[str] = ..., session_id: _Optional[str] = ..., lease_seconds: _Optional[int] = ..., prefetch_count: _Optional[int] = ...) -> None: ...

class ConsumeSessionAck(_message.Message):
    __slots__ = ("lock_id",)
    LOCK_ID_FIELD_NUMBER: _ClassVar[int]
    lock_id: str
    def __init__(self, lock_id: _Optional[str] = ...) -> None: ...

class ConsumeSessionDeadLetter(_message.Message):
    __slots__ = ("lock_id",)
    LOCK_ID_FIELD_NUMBER: _ClassVar[int]
    lock_id: str
    def __init__(self, lock_id: _Optional[str] = ...) -> None: ...

class ConsumeSessionResponse(_message.Message):
    __slots__ = ("session_assigned", "delivered", "error", "session_lost")
    SESSION_ASSIGNED_FIELD_NUMBER: _ClassVar[int]
    DELIVERED_FIELD_NUMBER: _ClassVar[int]
    ERROR_FIELD_NUMBER: _ClassVar[int]
    SESSION_LOST_FIELD_NUMBER: _ClassVar[int]
    session_assigned: SessionAssigned
    delivered: SessionDelivered
    error: SessionError
    session_lost: SessionLost
    def __init__(self, session_assigned: _Optional[_Union[SessionAssigned, _Mapping]] = ..., delivered: _Optional[_Union[SessionDelivered, _Mapping]] = ..., error: _Optional[_Union[SessionError, _Mapping]] = ..., session_lost: _Optional[_Union[SessionLost, _Mapping]] = ...) -> None: ...

class SessionAssigned(_message.Message):
    __slots__ = ("session_id", "lease_expires_at")
    SESSION_ID_FIELD_NUMBER: _ClassVar[int]
    LEASE_EXPIRES_AT_FIELD_NUMBER: _ClassVar[int]
    session_id: str
    lease_expires_at: float
    def __init__(self, session_id: _Optional[str] = ..., lease_expires_at: _Optional[float] = ...) -> None: ...

class SessionDelivered(_message.Message):
    __slots__ = ("lock_id", "item_json", "priority", "lock_expires_at")
    LOCK_ID_FIELD_NUMBER: _ClassVar[int]
    ITEM_JSON_FIELD_NUMBER: _ClassVar[int]
    PRIORITY_FIELD_NUMBER: _ClassVar[int]
    LOCK_EXPIRES_AT_FIELD_NUMBER: _ClassVar[int]
    lock_id: str
    item_json: str
    priority: int
    lock_expires_at: float
    def __init__(self, lock_id: _Optional[str] = ..., item_json: _Optional[str] = ..., priority: _Optional[int] = ..., lock_expires_at: _Optional[float] = ...) -> None: ...

class SessionError(_message.Message):
    __slots__ = ("error_code", "message")
    ERROR_CODE_FIELD_NUMBER: _ClassVar[int]
    MESSAGE_FIELD_NUMBER: _ClassVar[int]
    error_code: str
    message: str
    def __init__(self, error_code: _Optional[str] = ..., message: _Optional[str] = ...) -> None: ...

class SessionLost(_message.Message):
    __slots__ = ("message",)
    MESSAGE_FIELD_NUMBER: _ClassVar[int]
    message: str
    def __init__(self, message: _Optional[str] = ...) -> None: ...
