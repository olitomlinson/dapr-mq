// Package integration runs the Go SDK against a real DaprMQ stack. It starts its own throwaway
// stack with Testcontainers (no docker compose, no pre-existing server), mirroring the .NET fixture
// in server/tests/DaprMQ.IntegrationTests/Infrastructure/DaprTestEnvironment.cs: Postgres, Dapr
// placement + scheduler, the DaprMQ API server and a daprd sidecar on one private network with
// dynamic host ports.
//
// Prerequisite: the API image must exist locally - build it with `./build-and-test.sh --skip-tests`
// from the repo root (default tag daprmq-api:test, override with DAPRMQ_API_IMAGE).
package integration

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"fmt"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"testing"
	"time"

	"github.com/moby/moby/api/types/container"
	daprmq "github.com/olitomlinson/dapr-mq/sdks/go"
	"github.com/testcontainers/testcontainers-go"
	"github.com/testcontainers/testcontainers-go/network"
	"github.com/testcontainers/testcontainers-go/wait"
)

const (
	daprVersion      = "1.18.4"
	postgresPassword = "test_password"
	startupTimeout   = 90 * time.Second
)

var (
	httpURL     string
	grpcAddress string
)

func TestMain(m *testing.M) {
	if exec.Command("docker", "info").Run() != nil {
		fmt.Println("SKIP: Docker daemon not available")
		os.Exit(0)
	}
	apiImage := os.Getenv("DAPRMQ_API_IMAGE")
	if apiImage == "" {
		apiImage = "daprmq-api:test"
	}
	if exec.Command("docker", "image", "inspect", apiImage).Run() != nil {
		fmt.Printf("FAIL: Docker image %s not found - run ./build-and-test.sh --skip-tests from the repo root first.\n", apiImage)
		os.Exit(1)
	}

	stop, err := startStack(context.Background(), apiImage)
	if err != nil {
		fmt.Println("FAIL: starting the DaprMQ stack:", err)
		stop()
		os.Exit(1)
	}
	code := m.Run()
	stop()
	os.Exit(code)
}

func startStack(ctx context.Context, apiImage string) (stop func(), err error) {
	var containers []testcontainers.Container
	var nw *testcontainers.DockerNetwork
	stop = func() {
		for i := len(containers) - 1; i >= 0; i-- {
			_ = containers[i].Terminate(context.Background())
		}
		if nw != nil {
			_ = nw.Remove(context.Background())
		}
	}

	nw, err = network.New(ctx)
	if err != nil {
		return stop, err
	}
	start := func(alias string, req testcontainers.ContainerRequest) (testcontainers.Container, error) {
		req.Networks = []string{nw.Name}
		req.NetworkAliases = map[string][]string{nw.Name: {alias}}
		c, err := testcontainers.GenericContainer(ctx, testcontainers.GenericContainerRequest{ContainerRequest: req, Started: true})
		if c != nil {
			containers = append(containers, c)
		}
		if err != nil {
			return nil, fmt.Errorf("%s: %w", alias, err)
		}
		return c, nil
	}

	schedulerDir, err := worldWritableDir("daprmq-scheduler-")
	if err != nil {
		return stop, err
	}
	blobstoreDir, err := worldWritableDir("daprmq-blobstore-")
	if err != nil {
		return stop, err
	}
	_, thisFile, _, _ := runtime.Caller(0)
	componentsDir := filepath.Join(filepath.Dir(thisFile), "../../../server/tests/DaprMQ.IntegrationTests/dapr-components")

	if _, err := start("postgres-db", testcontainers.ContainerRequest{
		Image: "public.ecr.aws/docker/library/postgres:16.2-alpine",
		Env:   map[string]string{"POSTGRES_DB": "actor_state", "POSTGRES_USER": "postgres", "POSTGRES_PASSWORD": postgresPassword},
		WaitingFor: wait.ForExec([]string{"pg_isready", "-U", "postgres", "-d", "actor_state"}).
			WithStartupTimeout(startupTimeout),
	}); err != nil {
		return stop, err
	}
	if _, err := start("dapr-placement", testcontainers.ContainerRequest{
		Image: "ghcr.io/dapr/dapr:" + daprVersion,
		Cmd:   []string{"./placement", "-port", "50005"},
	}); err != nil {
		return stop, err
	}
	if _, err := start("dapr-scheduler", testcontainers.ContainerRequest{
		Image: "ghcr.io/dapr/dapr:" + daprVersion,
		Cmd:   []string{"./scheduler", "--port", "50006", "--log-level", "info", "--etcd-data-dir", "/data/dapr-scheduler"},
		HostConfigModifier: func(hc *container.HostConfig) {
			hc.Binds = append(hc.Binds, schedulerDir+":/data/dapr-scheduler:rw")
		},
	}); err != nil {
		return stop, err
	}
	time.Sleep(2 * time.Second) // placement/scheduler have no health probe exposed to us; same grace period as the .NET fixture

	api, err := start("api-server", testcontainers.ContainerRequest{
		Image:        apiImage,
		ExposedPorts: []string{"5000/tcp", "5001/tcp"},
		Env: map[string]string{
			"ASPNETCORE_URLS":            "http://+:5000",
			"REGISTER_ACTORS":            "true",
			"DAPR_HTTP_ENDPOINT":         "http://dapr-sidecar:3500",
			"DAPR_GRPC_ENDPOINT":         "http://dapr-sidecar:50001",
			"Logging__LogLevel__Default": "Warning",
			"QUEUE_ACTOR_TYPE_NAME":      "QueueActor",
			"HTTP_SINK_ACTOR_TYPE_NAME":  "HttpSinkActor",
		},
	})
	if err != nil {
		return stop, err
	}
	if _, err := start("dapr-sidecar", testcontainers.ContainerRequest{
		Image: "ghcr.io/dapr/daprd:" + daprVersion,
		Cmd: []string{
			"./daprd", "--app-id", "daprmq-api", "--app-channel-address", "api-server", "--app-port", "5000",
			"--dapr-http-port", "3500", "--dapr-grpc-port", "50001",
			"--placement-host-address", "dapr-placement:50005", "--scheduler-host-address", "dapr-scheduler:50006",
			"--resources-path", "/tmp/dapr-components", "--config", "/tmp/dapr-components/config.yml", "--log-level", "info",
		},
		HostConfigModifier: func(hc *container.HostConfig) {
			hc.Binds = append(hc.Binds, componentsDir+":/tmp/dapr-components:ro", blobstoreDir+":/tmp/blobstore:rw")
		},
	}); err != nil {
		return stop, err
	}

	host, err := api.Host(ctx)
	if err != nil {
		return stop, err
	}
	httpPort, err := api.MappedPort(ctx, "5000/tcp")
	if err != nil {
		return stop, err
	}
	grpcPort, err := api.MappedPort(ctx, "5001/tcp")
	if err != nil {
		return stop, err
	}
	httpURL = fmt.Sprintf("http://%s:%s", host, httpPort.Port())
	grpcAddress = fmt.Sprintf("%s:%s", host, grpcPort.Port())

	// The daprmq.DaprMQ.operations signal over HTTP: queue operations can be served. Doesn't
	// write anything, unlike a probe enqueue.
	deadline := time.Now().Add(startupTimeout)
	for {
		resp, err := http.Get(httpURL + "/health/operations")
		if err == nil {
			resp.Body.Close()
			if resp.StatusCode == http.StatusOK {
				return stop, nil
			}
		}
		if time.Now().After(deadline) {
			return stop, fmt.Errorf("DaprMQ API + sidecar not ready after %s (last error: %v)", startupTimeout, err)
		}
		time.Sleep(500 * time.Millisecond)
	}
}

func worldWritableDir(prefix string) (string, error) {
	dir, err := os.MkdirTemp("", prefix)
	if err != nil {
		return "", err
	}
	return dir, os.Chmod(dir, 0o777)
}

func newClient(t *testing.T, options *daprmq.ClientOptions) *daprmq.Client {
	t.Helper()
	c, err := daprmq.NewClient(httpURL, grpcAddress, options)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = c.Close() })
	return c
}

func newQueueID() string {
	b := make([]byte, 16)
	_, _ = rand.Read(b)
	return "go-it-" + hex.EncodeToString(b)
}
