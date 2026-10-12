# DaprMQ load generator (tools/loadgen/README.md). Build from the repo root, which brings in the
# Python SDK for stream mode:
#   docker build -f tools/loadgen/Dockerfile -t daprmq-loadgen .
FROM python:3.12-slim
WORKDIR /app
COPY sdks/python /sdk
RUN pip install --no-cache-dir /sdk && rm -rf /sdk
COPY tools/loadgen/loadgen.py .
ENV PYTHONUNBUFFERED=1
ENTRYPOINT ["python", "loadgen.py"]
