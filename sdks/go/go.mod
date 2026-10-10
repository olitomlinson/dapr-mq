module github.com/olitomlinson/dapr-mq/sdks/go

go 1.24.4

// v0.1.0 predates the 0.0.0-<pre-release> versioning scheme; v0.1.1 only exists to publish this retraction.
retract [v0.1.0, v0.1.1]

require (
	google.golang.org/grpc v1.73.0
	google.golang.org/protobuf v1.36.6
)

require (
	golang.org/x/net v0.38.0 // indirect
	golang.org/x/sys v0.31.0 // indirect
	golang.org/x/text v0.23.0 // indirect
	google.golang.org/genproto/googleapis/rpc v0.0.0-20250324211829-b45e905df463 // indirect
)
