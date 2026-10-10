package com.daprmq.client.perf;

import com.daprmq.client.perf.Profiles.QueueDrainParams;
import com.daprmq.client.perf.Profiles.SessionDrainParams;
import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.CsvSource;

import java.io.IOException;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;

class ProfilesTest {
    private static JsonNode shared() throws IOException {
        return new ObjectMapper().readTree(PerfOptions.defaultOutDir().getParent().resolve("sdks/testing/perf/profiles.json").toFile());
    }

    private static List<String> scenarioOf(String profile) {
        if (Profiles.LOAD.containsKey(profile)) {
            Profiles.LoadParams load = Profiles.LOAD.get(profile);
            return List.of(load.id(), load.scenario(), load.key());
        }
        if (Profiles.QUEUE_DRAIN.containsKey(profile)) {
            return List.of("P-05", "queue-drain", Profiles.QUEUE_DRAIN.get(profile).key());
        }
        return List.of("P-04", "session-drain", Profiles.SESSION_DRAIN.get(profile).key());
    }

    @Test
    void everyProfileMatchesTheSharedFile() throws IOException {
        JsonNode profiles = shared().get("profiles");
        List<String> names = new ArrayList<>();
        profiles.fieldNames().forEachRemaining(names::add);
        assertEquals(names.stream().sorted().toList(), Profiles.all().stream().sorted().toList());

        for (String profile : names) {
            JsonNode expected = profiles.get(profile);
            assertEquals(List.of(expected.get("id").asText(), expected.get("name").asText(), expected.get("key").asText()), scenarioOf(profile), profile);
            assertEquals(expected.get("scale").asText(), Profiles.scaleOf(profile), profile);
        }
    }

    @Test
    void suitesMatchTheSharedFile() throws IOException {
        JsonNode suites = shared().get("suites");
        assertEquals(suites.size(), Profiles.SUITES.size());
        Profiles.SUITES.forEach((suite, profiles) -> {
            List<String> expected = new ArrayList<>();
            suites.get(suite).forEach(p -> expected.add(p.asText()));
            assertEquals(expected, profiles);
        });
    }

    @Test
    void sessionIdealRoundsSessionsUpToWholeSlotRounds() {
        assertEquals(5000, SessionDrainParams.full().idealSeconds(), 1e-9);
        assertEquals(10, SessionDrainParams.full().with(21, 10, 500, 20, 0).idealSeconds(), 1e-9);
    }

    @Test
    void concurrentSessionIdealIsBoundedByTheSlowerOfConsumingAndPublishing() {
        SessionDrainParams base = SessionDrainParams.full().with(4, 10, 1000, 4, 0);
        assertEquals(23.5, base.withPublish("concurrent", 2000, 500).idealSeconds(), 1e-9);
        assertEquals(10, base.withPublish("before", 2000, 500).idealSeconds(), 1e-9);
    }

    @ParameterizedTest
    @CsvSource({"0,100,false,100", "10,100,false,10", "500,100,false,100", "10,100,true,1"})
    void queueConcurrencyIsWhatBoundsHandlersRunningAtOnce(int handlers, int active, boolean strict, int expected) {
        assertEquals(expected, new QueueDrainParams(4, 1000, active, handlers, strict, 0, 0, 0, 0).concurrency());
    }

    @Test
    void queueIdealCoversASlowTailAndARateLimitedPublisher() {
        assertEquals(5, new QueueDrainParams(20, 100, 100, 0, false, 0, 0, 10, 5000).idealSeconds(), 1e-9);
        assertEquals(9.9, new QueueDrainParams(50, 100, 100, 0, false, 200, 0, 0, 0).idealSeconds(), 1e-9);
        assertNull(new QueueDrainParams(4, 0, 100, 0).idealSeconds());
    }

    @Test
    void suitesRunTheirProfilesWithThreeReplicasByDefaultForExtreme() {
        PerfOptions pr = PerfOptions.parse(new String[] {"--suite", "pr", "--env-label", "ci-x"});
        assertEquals(Profiles.SUITES.get("pr"), pr.profiles());
        assertEquals(1, pr.apiReplicas());
        assertEquals("pr", pr.scale("enqueue"));
        assertEquals(3, PerfOptions.parse(new String[] {"--suite", "extreme"}).apiReplicas());
        assertEquals(5, PerfOptions.parse(new String[] {"--suite", "extreme", "--api-replicas", "5"}).apiReplicas());
        assertEquals("extreme", PerfOptions.parse(new String[] {"--profile", "enqueue-ramp"}).scale("enqueue-ramp"));
    }

    @Test
    void anExistingServerNeedsBothEndpointsAndNoReplicas() {
        assertThrows(IllegalArgumentException.class, () -> PerfOptions.parse(new String[] {"--http", "http://localhost:8002"}));
        assertThrows(IllegalArgumentException.class, () -> PerfOptions.parse(new String[] {"--http", "http://h", "--grpc", "g:1", "--api-replicas", "2"}));
        assertThrows(IllegalArgumentException.class, () -> PerfOptions.parse(new String[] {"--profile", "nope"}));
        assertEquals("localhost:8102", PerfOptions.parse(new String[] {"--http", "http://localhost:8002", "--grpc", "http://localhost:8102"}).grpc());
    }
}
