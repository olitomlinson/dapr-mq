from __future__ import annotations

import json
from typing import Any

import httpx


class FakeTransport(httpx.AsyncBaseTransport):
    """Minimal fake HTTP transport for exercising DaprMQClient's REST calls without a live server."""

    def __init__(self, status_code: int, json_body: Any = None) -> None:
        self.status_code = status_code
        self.json_body = json_body
        self.last_request: httpx.Request | None = None

    async def handle_async_request(self, request: httpx.Request) -> httpx.Response:
        self.last_request = request
        content = json.dumps(self.json_body).encode() if self.json_body is not None else b""
        return httpx.Response(
            self.status_code, content=content, headers={"content-type": "application/json"}, request=request
        )


def fake_client(status_code: int, json_body: Any = None) -> tuple[httpx.AsyncClient, FakeTransport]:
    transport = FakeTransport(status_code, json_body)
    return httpx.AsyncClient(transport=transport), transport
