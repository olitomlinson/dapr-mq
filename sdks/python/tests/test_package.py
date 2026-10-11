"""The published wheel must carry what PyPI and users need: README, licence and the generated gRPC stubs."""

import subprocess
import sys
import zipfile
from pathlib import Path

import pytest

SDK_DIR = Path(__file__).resolve().parent.parent


@pytest.fixture(scope="module")
def wheel(tmp_path_factory: pytest.TempPathFactory) -> zipfile.ZipFile:
    out = tmp_path_factory.mktemp("dist")
    subprocess.run([sys.executable, "-m", "build", "--wheel", "--outdir", str(out), str(SDK_DIR)], check=True)
    [path] = out.glob("*.whl")
    with zipfile.ZipFile(path) as zf:
        yield zf


def _metadata(wheel: zipfile.ZipFile) -> str:
    [name] = [n for n in wheel.namelist() if n.endswith(".dist-info/METADATA")]
    return wheel.read(name).decode()


def test_metadata_has_readme_and_licence(wheel: zipfile.ZipFile) -> None:
    metadata = _metadata(wheel)
    assert "License-Expression: Apache-2.0" in metadata
    assert "Description-Content-Type: text/markdown" in metadata
    assert "pip install daprmq-client" in metadata
    assert "Project-URL: Repository, https://github.com/olitomlinson/dapr-mq" in metadata


def test_ships_grpc_stubs(wheel: zipfile.ZipFile) -> None:
    names = wheel.namelist()
    assert "daprmq_client/grpc/daprmq_pb2.py" in names
    assert "daprmq_client/grpc/daprmq_pb2_grpc.py" in names


# httpx 1.0 drops AsyncClient; its dev releases already do, and `pip install --pre` picks them up.
def test_caps_httpx_below_1(wheel: zipfile.ZipFile) -> None:
    assert "Requires-Dist: httpx<1,>=0.27" in _metadata(wheel)
