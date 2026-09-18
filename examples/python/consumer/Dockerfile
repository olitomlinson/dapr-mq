# Build from the repo root:
#   docker build -f examples/python/consumer/Dockerfile -t daprmq-example-python-consumer .
FROM python:3.12-slim
WORKDIR /app
COPY sdks/python/ /sdks/python/
RUN pip install --no-cache-dir /sdks/python
COPY examples/python/consumer/ /app/
RUN pip install --no-cache-dir aiohttp
EXPOSE 8080
ENTRYPOINT ["python", "app.py"]
