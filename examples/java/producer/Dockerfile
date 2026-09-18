# Build with the repo root as context:
#   docker build -f examples/java/producer/Dockerfile -t daprmq-example-java-producer .
FROM maven:3.9-eclipse-temurin-17 AS build
WORKDIR /build

# sdks/java/pom.xml's protobuf-maven-plugin reads daprmq.proto from
# ../../server/src/DaprMQ.ApiServer/Protos (relative to sdks/java/), the same shared
# contract file the .NET/TypeScript/Python SDKs use - it has to be copied in at that
# same relative path or the gRPC stub generation silently finds no .proto files.
COPY server/src/DaprMQ.ApiServer/Protos/ ./server/src/DaprMQ.ApiServer/Protos/
COPY sdks/java/ ./sdks/java/
RUN mvn -f sdks/java/pom.xml install -DskipTests -B
COPY examples/java/producer/ ./examples/java/producer/
RUN mvn -f examples/java/producer/pom.xml package -B

FROM eclipse-temurin:17-jre
WORKDIR /app
COPY --from=build /build/examples/java/producer/target/daprmq-example-producer-0.1.0-shaded.jar app.jar
EXPOSE 8080
ENTRYPOINT ["java", "-jar", "app.jar"]
