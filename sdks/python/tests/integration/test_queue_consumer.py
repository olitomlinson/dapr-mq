"""QC-01..QC-06 from sdks/testing/INTEGRATION_TESTS.md - the managed QueueConsumer."""

from __future__ import annotations

import asyncio
import time

from daprmq_client import (
    DaprMQClient,
    EnqueueItem,
    QueueConsumer,
    QueueConsumerOptions,
    QueueHandlerFailureAction,
    QueueMessageContext,
)


async def enqueue_seqs(client: DaprMQClient, queue_id: str, *seqs: int) -> None:
    await client.enqueue(queue_id, [EnqueueItem({"seq": s}) for s in seqs])


async def wait_for(condition, because: str, timeout: float = 30) -> None:
    deadline = time.monotonic() + timeout
    while not condition():
        assert time.monotonic() < deadline, f"timed out waiting for {because}"
        await asyncio.sleep(0.05)


async def test_qc01_handler_success_acks_and_the_queue_ends_empty(client: DaprMQClient, queue_id: str) -> None:
    await enqueue_seqs(client, queue_id, *range(1, 21))
    handled: list[int] = []

    async def handler(ctx: QueueMessageContext) -> None:
        handled.append(ctx.item["seq"])

    async with QueueConsumer(client, queue_id, QueueConsumerOptions(), handler):
        await wait_for(lambda: len(handled) == 20, "every message to be handled")

    assert sorted(handled) == list(range(1, 21))
    assert await client.dequeue_locked(queue_id, allow_competing_consumers=True) is None


async def test_qc02_handler_error_nack_redelivers_with_delivery_count_2(client: DaprMQClient, queue_id: str) -> None:
    await enqueue_seqs(client, queue_id, 1)
    counts: list[int] = []

    async def handler(ctx: QueueMessageContext) -> None:
        counts.append(ctx.delivery_count)
        if ctx.delivery_count == 1:
            raise RuntimeError("first attempt fails")

    async with QueueConsumer(client, queue_id, QueueConsumerOptions(), handler):
        await wait_for(lambda: len(counts) == 2, "the nacked message to be redelivered")

    assert counts == [1, 2]
    assert await client.dequeue_locked(queue_id, allow_competing_consumers=True) is None


async def test_qc02_handler_error_dead_letter_moves_it_to_the_dead_letter_queue(client: DaprMQClient, queue_id: str) -> None:
    await enqueue_seqs(client, queue_id, 7)

    async def handler(_ctx: QueueMessageContext) -> None:
        raise RuntimeError("poison")

    async with QueueConsumer(client, queue_id, QueueConsumerOptions(on_handler_error=QueueHandlerFailureAction.DEAD_LETTER), handler):
        dead = None
        deadline = time.monotonic() + 30
        while dead is None:
            assert time.monotonic() < deadline, "timed out waiting for the dead-letter queue"
            dead = await client.dequeue_locked(f"{queue_id}-deadletter")
            await asyncio.sleep(0.1)

    assert [i.item["seq"] for i in dead.items] == [7]


async def test_qc03_max_concurrent_handlers_is_never_exceeded(client: DaprMQClient, queue_id: str) -> None:
    await enqueue_seqs(client, queue_id, *range(1, 11))
    running = peak = handled = 0

    async def handler(_ctx: QueueMessageContext) -> None:
        nonlocal running, peak, handled
        running += 1
        peak = max(peak, running)
        await asyncio.sleep(0.1)
        running -= 1
        handled += 1

    async with QueueConsumer(client, queue_id, QueueConsumerOptions(max_concurrent_handlers=2), handler):
        await wait_for(lambda: handled == 10, "every message to be handled")

    assert peak == 2


async def test_qc04_strict_order_handles_in_queue_order_including_after_a_nack(client: DaprMQClient, queue_id: str) -> None:
    await enqueue_seqs(client, queue_id, 1, 2, 3, 4, 5)
    succeeded: list[int] = []
    failed_once = False

    async def handler(ctx: QueueMessageContext) -> None:
        nonlocal failed_once
        if ctx.item["seq"] == 2 and not failed_once:
            failed_once = True
            raise RuntimeError("nack 2 once")
        succeeded.append(ctx.item["seq"])

    async with QueueConsumer(client, queue_id, QueueConsumerOptions(strict_order=True), handler):
        await wait_for(lambda: len(succeeded) == 5, "every message to be handled")

    assert succeeded == [1, 2, 3, 4, 5]


async def test_qc05_stop_drains_running_handlers_and_returns_unstarted_messages_straight_away(
    client: DaprMQClient, queue_id: str
) -> None:
    await enqueue_seqs(client, queue_id, 1, 2, 3)
    started = asyncio.Event()
    handled: list[int] = []

    async def handler(ctx: QueueMessageContext) -> None:
        started.set()
        await asyncio.sleep(0.5)
        handled.append(ctx.item["seq"])

    consumer = QueueConsumer(
        client, queue_id, QueueConsumerOptions(max_concurrent_handlers=1, max_active_messages=10, lock_ttl_seconds=300), handler
    )
    consumer.start()
    await asyncio.wait_for(started.wait(), timeout=30)
    await consumer.stop()

    assert handled == [1]
    # Well inside the 300 s lock, so only the stream's close can have returned them.
    back = await client.dequeue_locked(queue_id, count=10, allow_competing_consumers=True)
    assert back is not None and [i.item["seq"] for i in back.items] == [2, 3]


class TcpProxy:
    """Forwards a local port to the server so QC-06 can break every open connection without
    restarting a container, which would re-map its host ports."""

    def __init__(self, target: str) -> None:
        self._host, port = target.rsplit(":", 1)
        self._port = int(port)
        self._writers: list[asyncio.StreamWriter] = []
        self._server: asyncio.Server | None = None

    async def start(self) -> str:
        self._server = await asyncio.start_server(self._accept, "127.0.0.1", 0)
        return f"127.0.0.1:{self._server.sockets[0].getsockname()[1]}"

    async def _accept(self, in_reader: asyncio.StreamReader, in_writer: asyncio.StreamWriter) -> None:
        out_reader, out_writer = await asyncio.open_connection(self._host, self._port)
        self._writers += [in_writer, out_writer]
        await asyncio.gather(self._pipe(in_reader, out_writer), self._pipe(out_reader, in_writer))

    @staticmethod
    async def _pipe(reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        try:
            while data := await reader.read(65536):
                writer.write(data)
                await writer.drain()
        except Exception:
            pass
        finally:
            writer.close()

    def break_connections(self) -> None:
        """Drops every connection open now; new ones are still accepted."""
        for writer in self._writers:
            writer.transport.abort()
        self._writers.clear()

    async def close(self) -> None:
        self.break_connections()
        if self._server is not None:
            self._server.close()


async def test_qc06_broken_stream_reconnects_and_every_message_is_handled_at_least_once(daprmq_server, queue_id: str) -> None:
    proxy = TcpProxy(daprmq_server.grpc_address)
    address = await proxy.start()
    handled: dict[int, int] = {}

    async def handler(ctx: QueueMessageContext) -> None:
        await asyncio.sleep(0.05)
        handled[ctx.item["seq"]] = handled.get(ctx.item["seq"], 0) + 1

    try:
        async with DaprMQClient(http_base_url=daprmq_server.http_url, grpc_address=address) as client:
            await enqueue_seqs(client, queue_id, *range(1, 21))
            options = QueueConsumerOptions(max_active_messages=5, max_concurrent_handlers=2)
            async with QueueConsumer(client, queue_id, options, handler):
                await wait_for(lambda: len(handled) >= 5, "some messages to be handled before the break")
                proxy.break_connections()
                await wait_for(lambda: len(handled) == 20, "every message to be handled after reconnecting")

            assert sorted(handled) == list(range(1, 21))
            assert await client.dequeue_locked(queue_id, allow_competing_consumers=True) is None
    finally:
        await proxy.close()
