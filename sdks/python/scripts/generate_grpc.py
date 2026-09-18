#!/usr/bin/env python3
"""Regenerates src/daprmq_client/grpc/*_pb2*.py from the shared proto contract.

Single source of truth stays server/src/DaprMQ.ApiServer/Protos/daprmq.proto (mirrors how
sdks/dotnet/src/DaprMQ.Client references the same file, and sdks/typescript loads it at
runtime, rather than duplicating the contract here).
"""

import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[3]
PROTO_DIR = ROOT / "server" / "src" / "DaprMQ.ApiServer" / "Protos"
OUT_DIR = ROOT / "sdks" / "python" / "src" / "daprmq_client" / "grpc"


def main() -> None:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    subprocess.run(
        [
            sys.executable,
            "-m",
            "grpc_tools.protoc",
            f"-I{PROTO_DIR}",
            f"--python_out={OUT_DIR}",
            f"--grpc_python_out={OUT_DIR}",
            f"--pyi_out={OUT_DIR}",
            str(PROTO_DIR / "daprmq.proto"),
        ],
        check=True,
    )

    # protoc emits `import daprmq_pb2 as daprmq__pb2` (top-level, not package-relative) - fix up
    # to a relative import so the generated module works when imported as daprmq_client.grpc.*.
    grpc_file = OUT_DIR / "daprmq_pb2_grpc.py"
    text = grpc_file.read_text()
    text = text.replace("import daprmq_pb2 as daprmq__pb2", "from . import daprmq_pb2 as daprmq__pb2")
    grpc_file.write_text(text)

    (OUT_DIR / "__init__.py").touch()


if __name__ == "__main__":
    main()
