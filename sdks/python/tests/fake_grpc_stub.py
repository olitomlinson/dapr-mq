from __future__ import annotations

import asyncio

from daprmq_client.grpc import daprmq_pb2


class FakeStreamStreamCall:
    """Minimal fake standing in for grpc.aio's bidi StreamStreamCall in tests."""

    def __init__(self) -> None:
        self.written: list[daprmq_pb2.ConsumeSessionRequest] = []
        self._queue: asyncio.Queue[object] = asyncio.Queue()
        self._done_writing = False
        self._cancelled = False

    async def write(self, message: daprmq_pb2.ConsumeSessionRequest) -> None:
        self.written.append(message)

    async def done_writing(self) -> None:
        self._done_writing = True

    def cancel(self) -> bool:
        self._cancelled = True
        self._queue.put_nowait(StopAsyncIteration())
        return True

    @property
    def cancelled(self) -> bool:
        return self._cancelled

    def emit(self, response: daprmq_pb2.ConsumeSessionResponse) -> None:
        """Test helper: deliver a server frame."""
        self._queue.put_nowait(response)

    def emit_end(self) -> None:
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
