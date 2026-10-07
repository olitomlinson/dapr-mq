package com.daprmq.client;

import io.grpc.ManagedChannel;
import io.grpc.Server;
import io.grpc.Status;
import io.grpc.health.v1.HealthCheckRequest;
import io.grpc.health.v1.HealthCheckResponse;
import io.grpc.health.v1.HealthCheckResponse.ServingStatus;
import io.grpc.health.v1.HealthGrpc;
import io.grpc.inprocess.InProcessChannelBuilder;
import io.grpc.inprocess.InProcessServerBuilder;
import io.grpc.stub.StreamObserver;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;

import java.io.IOException;
import java.net.http.HttpClient;
import java.time.Duration;
import java.util.List;
import java.util.concurrent.CopyOnWriteArrayList;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

/** waitForReady (sdks/testing/RETRIES_AND_READINESS.md): grpc.health.v1 Watch until SERVING. */
class DaprMQClientWaitForReadyTest {
    /** One scripted Watch stream: statuses then complete, statuses then stay open, or an error. */
    private record Script(List<ServingStatus> statuses, boolean hang, Status error) {
        static Script statuses(ServingStatus... s) {
            return new Script(List.of(s), false, null);
        }

        static Script hang(ServingStatus... s) {
            return new Script(List.of(s), true, null);
        }

        static Script error(Status status) {
            return new Script(List.of(), false, status);
        }
    }

    private Server server;
    private ManagedChannel channel;
    private final List<String> services = new CopyOnWriteArrayList<>();

    private DaprMQClient client(Script... scripts) throws IOException {
        List<Script> queue = new CopyOnWriteArrayList<>(List.of(scripts));
        String name = InProcessServerBuilder.generateName();
        server = InProcessServerBuilder.forName(name).directExecutor().addService(new HealthGrpc.HealthImplBase() {
            @Override
            public void watch(HealthCheckRequest request, StreamObserver<HealthCheckResponse> observer) {
                services.add(request.getService());
                Script script = queue.size() > 1 ? queue.remove(0) : queue.get(0);
                if (script.error() != null) {
                    observer.onError(script.error().asRuntimeException());
                    return;
                }
                script.statuses().forEach(s -> observer.onNext(HealthCheckResponse.newBuilder().setStatus(s).build()));
                if (!script.hang()) {
                    observer.onCompleted();
                }
            }
        }).build().start();
        channel = InProcessChannelBuilder.forName(name).directExecutor().build();
        return new DaprMQClient("http://localhost:1", HttpClient.newHttpClient(), null, HealthGrpc.newBlockingStub(channel), RetryOptions.defaults());
    }

    @AfterEach
    void tearDown() {
        if (channel != null) {
            channel.shutdownNow();
        }
        if (server != null) {
            server.shutdownNow();
        }
    }

    @Test
    void notServingThenServingReturnsAndWatchesTheOperationsService() throws Exception {
        assertTrue(client(Script.statuses(ServingStatus.NOT_SERVING, ServingStatus.SERVING)).waitForReady(Duration.ofSeconds(5)));

        assertEquals(List.of("daprmq.DaprMQ.operations"), services);
    }

    @Test
    void anotherServiceCanBeWatched() throws Exception {
        assertTrue(client(Script.statuses(ServingStatus.SERVING)).waitForReady("daprmq.DaprMQ", Duration.ofSeconds(5)));

        assertEquals(List.of("daprmq.DaprMQ"), services);
    }

    @Test
    void unavailableThenServingReconnects() throws Exception {
        assertTrue(client(Script.error(Status.UNAVAILABLE), Script.statuses(ServingStatus.SERVING)).waitForReady(Duration.ofSeconds(5)));

        assertEquals(2, services.size());
    }

    @Test
    void streamEndingBeforeServingReconnects() throws Exception {
        assertTrue(client(Script.statuses(ServingStatus.NOT_SERVING), Script.statuses(ServingStatus.SERVING)).waitForReady(Duration.ofSeconds(5)));

        assertEquals(2, services.size());
    }

    @Test
    void unimplementedThrowsUnsupported() throws Exception {
        DaprMQClient client = client(Script.error(Status.UNIMPLEMENTED));

        assertThrows(UnsupportedOperationException.class, () -> client.waitForReady(Duration.ofSeconds(5)));
    }

    @Test
    void neverServingReturnsFalseWhenTheTimeoutRunsOut() throws Exception {
        assertFalse(client(Script.hang(ServingStatus.NOT_SERVING)).waitForReady(Duration.ofMillis(200)));
    }
}
