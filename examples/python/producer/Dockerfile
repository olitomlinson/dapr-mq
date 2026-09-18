# Build from the repo root:
#   docker build -f examples/python/producer/Dockerfile -t daprmq-example-python-producer .
FROM python:3.12-slim
WORKDIR /app
COPY sdks/python/ /sdks/python/
RUN pip install --no-cache-dir /sdks/python
COPY examples/python/producer/ /app/
RUN pip install --no-cache-dir aiohttp
EXPOSE 8080
ENTRYPOINT ["python", "app.py"]
