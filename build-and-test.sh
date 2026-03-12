#!/usr/bin/env bash
set -euo pipefail

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Script directory (where this script is located)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Default configuration
DOCKER_IMAGE_NAME="daprmq-api"
DOCKER_IMAGE_TAG="${DOCKER_IMAGE_TAG:-test}"
SKIP_BUILD="${SKIP_BUILD:-false}"
SKIP_PUSH="${SKIP_PUSH:-true}"
SKIP_TESTS="${SKIP_TESTS:-false}"
DOCKER_REGISTRY="${DOCKER_REGISTRY:-}"
VERBOSE="${VERBOSE:-false}"
QUEUE_ID="${QUEUE_ID:-}"
ENABLE_CONTAINER_LOGS="${ENABLE_CONTAINER_LOGS:-false}"

# Parse command line arguments
show_help() {
    cat << EOF
Usage: $0 [OPTIONS]

Build Docker image and run integration tests for DaprMQ

OPTIONS:
    -h, --help              Show this help message
    -t, --tag TAG           Docker image tag (default: test)
    -r, --registry REGISTRY Docker registry URL (e.g., ghcr.io/username)
    -p, --push              Push Docker image to registry
    -q, --queue-id ID       Queue ID for tests (default: random UUID)
    --skip-build            Skip Docker image build
    --skip-push             Skip Docker image push (default)
    --skip-tests            Skip integration tests
    --enable-logs           Enable container logs in tests
    -v, --verbose           Enable verbose output

EXAMPLES:
    # Build and test locally (default)
    $0

    # Build, push to registry, and test
    $0 --push --registry ghcr.io/myuser --tag v1.0.0

    # Run tests only (skip build)
    $0 --skip-build

    # Build only (skip tests)
    $0 --skip-tests

    # Run tests with specific queue ID
    $0 --queue-id my-test-queue

    # Run tests with container logs enabled
    $0 --enable-logs

ENVIRONMENT VARIABLES:
    DOCKER_IMAGE_TAG        Override image tag
    DOCKER_REGISTRY         Override registry
    SKIP_BUILD              Skip build step (true/false)
    SKIP_PUSH               Skip push step (true/false)
    SKIP_TESTS              Skip tests (true/false)
    VERBOSE                 Enable verbose output (true/false)
    QUEUE_ID                Queue ID for tests (random if not set)
    ENABLE_CONTAINER_LOGS   Enable container logs (true/false, default: false)

EOF
}

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        -h|--help)
            show_help
            exit 0
            ;;
        -t|--tag)
            DOCKER_IMAGE_TAG="$2"
            shift 2
            ;;
        -r|--registry)
            DOCKER_REGISTRY="$2"
            shift 2
            ;;
        -p|--push)
            SKIP_PUSH="false"
            shift
            ;;
        -q|--queue-id)
            QUEUE_ID="$2"
            shift 2
            ;;
        --skip-build)
            SKIP_BUILD="true"
            shift
            ;;
        --skip-push)
            SKIP_PUSH="true"
            shift
            ;;
        --skip-tests)
            SKIP_TESTS="true"
            shift
            ;;
        --enable-logs)
            ENABLE_CONTAINER_LOGS="true"
            shift
            ;;
        -v|--verbose)
            VERBOSE="true"
            shift
            ;;
        *)
            echo -e "${RED}Unknown option: $1${NC}"
            show_help
            exit 1
            ;;
    esac
done

# Construct full image name
if [[ -n "$DOCKER_REGISTRY" ]]; then
    FULL_IMAGE_NAME="${DOCKER_REGISTRY}/${DOCKER_IMAGE_NAME}:${DOCKER_IMAGE_TAG}"
else
    FULL_IMAGE_NAME="${DOCKER_IMAGE_NAME}:${DOCKER_IMAGE_TAG}"
fi

# Logging functions
log_info() {
    echo -e "${BLUE}[INFO]${NC} $*"
}

log_success() {
    echo -e "${GREEN}[SUCCESS]${NC} $*"
}

log_warning() {
    echo -e "${YELLOW}[WARNING]${NC} $*"
}

log_error() {
    echo -e "${RED}[ERROR]${NC} $*"
}

log_section() {
    echo ""
    echo -e "${BLUE}========================================${NC}"
    echo -e "${BLUE}$*${NC}"
    echo -e "${BLUE}========================================${NC}"
    echo ""
}

# Check prerequisites
check_prerequisites() {
    log_section "Checking Prerequisites"

    local missing_tools=()

    if ! command -v docker &> /dev/null; then
        missing_tools+=("docker")
    fi

    if ! command -v dotnet &> /dev/null; then
        missing_tools+=("dotnet")
    fi

    if [[ ${#missing_tools[@]} -gt 0 ]]; then
        log_error "Missing required tools: ${missing_tools[*]}"
        log_error "Please install the missing tools and try again"
        exit 1
    fi

    log_success "All prerequisites installed"

    # Check Docker daemon
    if ! docker info &> /dev/null; then
        log_error "Docker daemon is not running"
        exit 1
    fi

    log_info "Docker version: $(docker --version)"
    log_info ".NET version: $(dotnet --version)"
}

# Build Docker image
build_docker_image() {
    log_section "Building Docker Image"

    log_info "Image: $FULL_IMAGE_NAME"
    log_info "Context: $SCRIPT_DIR/dotnet"

    local build_args=(
        "build"
        "-t" "$FULL_IMAGE_NAME"
        "-f" "$SCRIPT_DIR/dotnet/Dockerfile"
    )

    if [[ "$VERBOSE" == "true" ]]; then
        build_args+=("--progress=plain")
    fi

    build_args+=("$SCRIPT_DIR/dotnet")

    if docker "${build_args[@]}"; then
        log_success "Docker image built successfully: $FULL_IMAGE_NAME"
    else
        log_error "Failed to build Docker image"
        exit 1
    fi
}

# Push Docker image to registry
push_docker_image() {
    log_section "Pushing Docker Image"

    if [[ -z "$DOCKER_REGISTRY" ]]; then
        log_error "Cannot push: No registry specified. Use --registry option"
        exit 1
    fi

    log_info "Pushing to: $FULL_IMAGE_NAME"

    if docker push "$FULL_IMAGE_NAME"; then
        log_success "Docker image pushed successfully"
    else
        log_error "Failed to push Docker image"
        log_warning "Make sure you're authenticated to the registry:"
        log_warning "  docker login $DOCKER_REGISTRY"
        exit 1
    fi
}

# Run integration tests
run_integration_tests() {
    log_section "Running Integration Tests"

    log_info "Test project: dotnet/tests/DaprMQ.IntegrationTests"

    # Generate random queue ID if not provided
    if [[ -z "$QUEUE_ID" ]]; then
        QUEUE_ID="test-queue-$(uuidgen | tr '[:upper:]' '[:lower:]' | tr -d '-')"
        log_info "Generated queue ID: $QUEUE_ID"
    else
        log_info "Using queue ID: $QUEUE_ID"
    fi

    cd "$SCRIPT_DIR/dotnet"

    local test_args=(
        "test"
        "tests/DaprMQ.IntegrationTests/DaprMQ.IntegrationTests.csproj"
        "--configuration" "Release"
    )

    if [[ "$VERBOSE" == "true" ]]; then
        test_args+=(
            "--logger" "console;verbosity=detailed"
            "--verbosity" "normal"
        )
    else
        test_args+=(
            "--logger" "console;verbosity=normal"
        )
    fi

    log_info "Running: dotnet ${test_args[*]}"
    echo ""

    if DAPRMQ_TEST_QUEUE_ID="$QUEUE_ID" ENABLE_CONTAINER_LOGS="$ENABLE_CONTAINER_LOGS" dotnet "${test_args[@]}"; then
        log_success "All integration tests passed!"
    else
        log_error "Integration tests failed"
        exit 1
    fi
}

# Cleanup function
cleanup() {
    local exit_code=$?
    if [[ $exit_code -ne 0 ]]; then
        log_error "Script failed with exit code $exit_code"
    fi
    exit $exit_code
}

trap cleanup EXIT

# Main execution
main() {
    log_section "DaprMQ Build and Test"

    log_info "Configuration:"
    log_info "  Image: $FULL_IMAGE_NAME"
    log_info "  Skip Build: $SKIP_BUILD"
    log_info "  Skip Push: $SKIP_PUSH"
    log_info "  Skip Tests: $SKIP_TESTS"
    log_info "  Verbose: $VERBOSE"
    log_info "  Enable Container Logs: $ENABLE_CONTAINER_LOGS"
    if [[ -n "$QUEUE_ID" ]]; then
        log_info "  Queue ID: $QUEUE_ID"
    fi

    check_prerequisites

    # Build phase
    if [[ "$SKIP_BUILD" == "false" ]]; then
        build_docker_image
    else
        log_warning "Skipping Docker image build"
    fi

    # Push phase
    if [[ "$SKIP_PUSH" == "false" ]]; then
        push_docker_image
    else
        log_info "Skipping Docker image push"
    fi

    # Test phase
    if [[ "$SKIP_TESTS" == "false" ]]; then
        run_integration_tests
    else
        log_warning "Skipping integration tests"
    fi

    log_section "Summary"
    log_success "All requested operations completed successfully!"

    if [[ "$SKIP_BUILD" == "false" ]]; then
        log_info "Built image: $FULL_IMAGE_NAME"
    fi

    if [[ "$SKIP_PUSH" == "false" ]]; then
        log_info "Pushed to registry: $DOCKER_REGISTRY"
    fi

    if [[ "$SKIP_TESTS" == "false" ]]; then
        log_success "Integration tests: PASSED"
    fi
}

# Run main function
main
