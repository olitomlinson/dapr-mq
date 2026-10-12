# DaprMQ load generator (tools/loadgen/README.md). Standard library only.
FROM python:3.12-alpine
WORKDIR /app
COPY loadgen.py .
ENV PYTHONUNBUFFERED=1
ENTRYPOINT ["python", "loadgen.py"]
