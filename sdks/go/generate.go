package daprmq

// The gRPC stubs under internal/pb are generated from the shared proto contract and checked in.
// Re-run `go generate` after the proto changes (needs protoc, protoc-gen-go and protoc-gen-go-grpc).
//go:generate protoc --proto_path=../../server/src/DaprMQ.ApiServer/Protos --go_out=internal/pb --go_opt=paths=source_relative --go_opt=Mdaprmq.proto=github.com/olitomlinson/dapr-mq/sdks/go/internal/pb --go-grpc_out=internal/pb --go-grpc_opt=paths=source_relative --go-grpc_opt=Mdaprmq.proto=github.com/olitomlinson/dapr-mq/sdks/go/internal/pb daprmq.proto
