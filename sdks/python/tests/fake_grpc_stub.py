from __future__ import annotations

import asyncio

from daprmq_client.grpc import daprmq_pb2


class FakeStreamStreamCall:
    """Minimal fake standing in for grpc.aio's bidi StreamStreamCall in tests."""

    def __init__(self, *, ends_after_half_close: float | None = None, on_half_close=None) -> None:
        """``ends_after_half_close``: like the real server, end the response stream this many
        seconds after the client half-closes (None: never, unless the test ends it).
        ``on_half_close``: called when the client half-closes."""
        self.written: list[daprmq_pb2.ConsumeSessionRequest] = []
        self._queue: asyncio.Queue[object] = asyncio.Queue()
        self._done_writing = False
        self._cancelled = False
        self._ended = False
        self.cancelled_before_server_ended = False
        self._ends_after_half_close = ends_after_half_close
        self._on_half_close = on_half_close

    async def write(self, message: daprmq_pb2.ConsumeSessionRequest) -> None:
        if self._done_writing:
            raise RuntimeError("write after done_writing")
        self.written.append(message)

    async def done_writing(self) -> None:
        self._done_writing = True
        if self._on_half_close is not None:
            self._on_half_close()
        if self._ends_after_half_close is not None:
            asyncio.get_running_loop().call_later(self._ends_after_half_close, self.emit_end)

    def cancel(self) -> bool:
        self._cancelled = True
        self.cancelled_before_server_ended |= not self._ended
        self._queue.put_nowait(StopAsyncIteration())
        return True

    @property
    def cancelled(self) -> bool:
        return self._cancelled

    @property
    def half_closed(self) -> bool:
        return self._done_writing

    def emit(self, response: daprmq_pb2.ConsumeSessionResponse) -> None:
        """Test helper: deliver a server frame."""
        self._queue.put_nowait(response)

    def emit_end(self) -> None:
        self._ended = True
        self._queue.put_nowait(StopAsyncIteration())

    def __aiter__(self) -> "FakeStreamStreamCall":
        return self

    async def __anext__(self) -> daprmq_pb2.ConsumeSessionResponse:
        item = await self._queue.get()
        if isinstance(item, StopAsyncIteration):
            raise item
        return item


class FakeStub:
    def __init__(self, call: FakeStreamStreamCall) -> None:
        self._call = call

    def ConsumeSession(self) -> FakeStreamStreamCall:  # noqa: N802 - matches generated stub casing
        return self._call

    def Consume(self) -> FakeStreamStreamCall:  # noqa: N802 - matches generated stub casing
        return self._call
