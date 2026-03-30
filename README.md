# daprMQ

- A simple Queue implementation for JSON payloads
- Choose from either gauranteed FIFO consumers, or competing consumers (FIFO not gauranteed!)
- Bring your own control loop to process the queue, or use the built-in HTTP Sink to drive messages to an endpoint.

## Quick Start

Clone and start with Docker
```bash
git clone https://github.com/olitomlinson/dapr-mq.git

cd dapr-mq

docker-compose up
```

### Explore the API capabilities of DaprMQ via the bundled Dashboard

[http://localhost:9000](http://localhost:9000)

![Alt text of the image](/docs/dash.png)

### Call the Queue APIs directly

This is as simple as it gets. Use `push` to add a message to the Queue. Use `pop` to dequeue a message in your processing loop.

```bash
# Push a message to the queue with priority 1
curl -X POST http://localhost:8000/queue/my-queue/push \
  -H "Content-Type: application/json" \
  -d '{ "items": [{ "item": { "task": "first" }, "priority": 1 }] }'

# Push a message to the same queue, with priority 0
curl -X POST http://localhost:8000/queue/my-queue/push \
  -H "Content-Type: application/json" \
  -d '{ "items": [{ "item": { "task": "second"}, "priority": 0 }] }'

# Pop a message...
curl -X POST "http://localhost:8000/queue/my-queue/pop"
# {
#  "items": [
#    {
#      "item": {
#        "task": "second"
#      },
#      "priority": 0
#    }
#  ]
# }

# Pop a message...
curl -X POST "http://localhost:8000/queue/my-queue/pop"
# {
#  "items": [
#    {
#      "item": {
#        "task": "first"
#      },
#      "priority": 1
#    }
#  ]
# }
```

### Enable a HTTP Sink

If you don't require a specialist control loop to pull messages from the Queue, you can use the built-in HTTP Sink that will automatically forward messages to any HTTP endpoint. 

The HTTP Sink will obey several configurable parameters, such as Lock TTL, Polling Interval, and Max Concurrency, allowing you to fine-tune the throughput and latency.

```bash
curl -X POST 'http://localhost:8000/queue/my-queue/sink/http/register' \
  -H 'Content-Type: application/json' \
  -d '{
        "url": "http://my-wiremock-container:8090/api/message-reciever",
        "maxConcurrency": 5,
        "LockTtlSeconds" : 30,
        "PollingIntervalSeconds": 5
      }'
```

### Responding to a HTTP Request delivered by the HTTP Sink

The `url` that is called by the HTTP Sink **MUST** respond with one of following Status Codes
 - `200 OK` - The HTTP Sink will automically Ack the message
 - `202 Accepted` - The HTTP Sink will NOT automatically Ack the message - You have taken responsibility to complete the message by calling either `/acknowledge` or `/dead-letter` on the Queue.
 - `4xx` / `5xx` - The HTTP sink will take no action (No-op). The message will expire naturally via the `LockTtlSeconds` parameter, and then become available again for delivery at some point in the future.


### Run integration tests

```bash
./build-and-test.sh --enable-logs --queue-id my-unique-test-run
```

**See [docs/QUICKSTART.md](docs/QUICKSTART.md) for complete setup instructions and examples.**

## What's in the box?

This API provides a ready-to-use queue abstraction:

- **It just works!**: Two endpoints to get started (`Push`, `Pop`) - that's it.
- **Priority Support**: Route urgent messages ahead of normal ones (0 = highest priority, default: 1).
- **Flexible API**: Use via a HTTP API, gRPC API, or direct via Dapr Actor SDK (JavaScript, Python, Java, Dotnet, Go)
- **Acknowledgements / at-least-once delivery**: Consumers can optionally specify that `Pop` requires a follow-on `Ack` - If the `Ack` is not received within a timeout period, the message is made available at the next `Pop`.
- **Bulk Push & Bulk Pop**: Atomic operations for pushing and popping many items, for best throughput.
- **Competing Consumers**: Have many pop operations concurrently in a traditional competing consumer pattern. This requires the use of Acknowledgements. **FIFO is not gauranteed when using competing consumers.**
- **Dead-lettering**: When using Acknowledgements, you can call `/dead-letter` to move the poison message to a dedicated deadletter queue, allowing subsequent `pop` operations to continue
- **On-Demand**: Queues are created on-demand at the time of first `Pop` or `Push`, there is no need to create them AOT.
- **Scalable**: Built on top of Dapr Virtual Actors, giving out-of-the-box High Availability and Horizontal scaling.
- **Persistent**: Backed by any Dapr state store (PostgreSQL, Redis, Cosmos DB, etc.)

Perfect for task scheduling, message buffering, event sourcing, or any scenario where you need ordered, priority-based processing with strict transactional guarantees.

## Comparing with Kafka at high-level

Kafka is excellent for high-throughput streaming workloads, but it relies on **partitions** to maintain ordering.

In multi-tenant systems this can introduce the **noisy neighbour problem** where one tenant blocks a partition and delays others.

daprMQ takes a different approach:

| Feature | Kafka | daprMQ |
|-------|------|------|
| Ordering | Per partition | Per queue |
| Priority | No native support | Built-in |
| Dead-letter | Application managed | Built-in |
| Multi-tenant isolation | Partition sharing | Per-tenant queue |

## Comparing with Kafka (going deeper)

- Kafka guarantees in-order processing of Topics via Partitions, which works incredibly well for high-throughput, single-tenant usecases. However, Partition-based technologies do not flex well to support in-order processing of multi-tenant shared-process systems. It's common that one Partition becomes blocked (due to one or more tenants saturating the partition), causing indefinite delays to other tenants that may be further in the tail in the back of the Partition. This creates what is is known as a Noisy Neighbor problem, and predictable Quality of Service becomes hard to deliver. Increasing the number of Partitions can alleviate the statistical likelihood of Noisy Neighbors, however it can never fully solve it unless number of tenants is less than the number of Partitions. Partitions are heavy weight constructs in Kafka and there are practical limits to how many partitions can run on a single broker ([Ask Chat GPT about the theoretical & practical limits of Kafka Partitions](https://chatgpt.com/share/69aef195-b104-8008-bf40-4b281a430f44))

  **Ultimately, daprMQ is not bound by partition-based architecture. Queues in this solution are extremely cheap and lightweight (unlike Kafka Topics). This means that rather than having a multi-tenant Topic, you can have discrete Queues for each tenant and sub-tenant process. These queues can be as short-lived or as long-lived as you like. If you have an event-based system where you must act on a specific tenant, then you can go straight to the tenanted-queue in question and just `Pop` it until its empty.**. 

- Kafka has no native concept of a dead-letter queue, which is a very reasonable position given that Kafka is rooted as a Streaming technology. This forces Application developers to implement their own Dead-lettering mechanism, which is often an after thought. In Kafka, Application Developers will typically choose to drop messages to keep the Partition unblocked, or continually retry the message, without advancing the offset. As described above, blocking Partitions is considered bad for a multi-tenant system, so this drives Application Developers into an awkward problem-space where there are no good practical solution when handling a poison-message, other than relax the in-order guarantees which may be easier said than done, or very quickly patch problematic consumers to handle a poison message.

  **In this regard, daprMQ operates more like a transactional message broker, than a streaming solution. As there are no Partitions, there is no risk of a single queue blocking the progress of another queue the system. daprMQ has a first-class deadletter capability, so that messages can transactionally be moved to a holding queue. (As of now, only messages that have been Popped with a required Acknowledgement operation can be moved to the Dead-letter queue)**

- Kafka has no built-in concept of priority messaging, which once again, is very reasonable position given it is a Streaming technology built ontop of Partitions. There are several strategies to accomplish a priority messaging concept, but they are not lightweight. e.g. Building ones own priority system atop of Kafka Topics, or repartitioning the streams with middleware etc.

  **Each daprMQ has a virtually-unlimited amount of sub-queues which represent priority. Messages are by default pushed into a Priority 1 sub-queue, but the range is from 0 - 2,147,483,647. Application Developers have complete control over the priority when *Pushing* a message.**

## Roadmap

### Done

- [x] **Bulk Push**: Write many messages to a queue in one atomic operation.
- [x] **Bulk Pop**: Pop many message from a queue in one atomic operation.
- [x] **Competing Consumers**: Experimental project (requires Bulk Push and Bulk Pop as a pre-requisite)

### To-do

- [ ] **Multi-tenant API surface**: Ensure all queues operations can be scoped to a first-class `Tenant ID`. Stretch to JWT validation.
- [ ] **Message Deduplication**: Messages with a non-unique Idempotency Key will be deuplicated within a time-window since first-occurence.
- [ ] **Fan-Out Architecture**: When publishing to a single queue, it will be possible to fan-out to N other queues. This would form the underpinnings of a real Pub Sub system, whereby Publishers are unaware of Subscribers.
- [ ] **Optimised large message support**: large messages are already possible, but ideally large messages should not be stored directly in the hot storage tier, but instead offloaded to a binary store (requires Dapr Binary store capability, or similar)
- [ ] **Queue Purge**: Remove all messages from a queue.
- [ ] **Max Retries / Automatic Dead-lettering**: When calling Pop With Acknowledgements, if a message is popped N number of times without Acknowledgement, the message will be deemed poisonous, and automatically moved to the Deadletter queue.
- [ ] **Dead-letter Forwarding**: Send a HTTP notification to any endpoint when a dead-letter message has been produced. Useful for alerting Operators, and triggering automated healing routines that sit external to the system.
- [ ] **Priority Delete**: Delete a priority sub-queue in one operation.
- [ ] **Scheduled Enque**: A message can be pushed to the front of any sub-queue, at a future scheduled time (Requires Message Receipts feature)
- [ ] **Message revocation**: A message can be removed from the queue, regardless of its position (Requires Message Receipts feature)
- [ ] **Rate-limit**: Push and Pop operations are protected with a per queue rate-limiter.
- [ ] **Qoutas**: Queues are governed by Push and Pop qouates per minute/hour/day.
- [ ] **Queue capacity limit**: Prevent Pushes until queue length drops below a capacity limit.
- [ ] **Queue reorder functions**: Reorder a queue based on a field within the Json.
- [ ] **Message functions**: Before a Pop is about to occur, call a HTTP endpoint with the message payload and replace message payload with the HTTP response.
- [ ] **Language SDKs**: Competing Consumers would benefit from language SDKs which implement a gRPC message pump loop. This would allow the SDK to buffer messages and relay them to the applications message handler as push-based model to further increase throughput and reduce latency.
- [ ] **Message TTL**: After a message has been in the queue for longer than the target ttl, drop it or send it to the dlq.
- [ ] **Message Receipts**: on successful publish, return a unique receipt id which encodes the segment, and maybe even position


## Use Cases

- **Task Queues**: Background job processing with priority levels
- **Message Buffering**: Temporary storage between microservices
- **Event Sourcing**: Store events for ordered replay
- **Workload Distribution**: Route tasks to priority-based worker pools

## Why the Dapr dependency?

This project leverages two Dapr building blocks, Dapr **Actors** and Dapr **State Store**. With a future dependency on Dapr **Jobs** Building Block.

Dapr Virtual Actors are key architectural focus, and each Queue is represented by one Dapr Virtual Actor.

Virtual Actors come with built-in **horizontal scaling** and node rebalancing, turn-based **single-threaded concurrency model** and a **Key Value sub-system**, which is essentially a write-thru cache to a persistent store. 

The persistent Store can be any number of stores supported by the Dapr OSS project (most notably, Postgres, Redis, MySql, MS-SQL, MongoDb, Oracle, DnyamoDb, CosmosDb)

Dapr Actors are extremely lightweight. Therefore hundreds of thousands of them can be active in-memory keeping the latencies of `Push` and `Pop` operations as low as possible. 

Each `Push` or `Pop` operation will create just a single transactional operation to the underlying persistent store, and often times will be a just 2 database operations in that transaction. 

You can consider this project to be IO bound more than cpu or memory bound, as IO with the underlying store is the most significant contributor to overall E2E operation time.

Actors will remain in memory while they are being used. However, after a configurable period of inactivity an Actor becomes eligible for deactivation, and will be deactivated without dataloss.

A theoretically unbounded number actors can exist in a deacivated state in persistent storage. Making any operation on a deactivated Actor will cause it to hydrate into memory and become active & usable. The Actor lifecycle happens transparently and is handled by the Dapr runtime, and is highly optimised for low-latency.

OSS Dapr language SDKs can interoperate **directly** with the Actors in this project, or interoperation can be achieved via an opinionated API server that is exposed over both HTTP and GRPC, which is recommended as a starting point.

### Architecture Overview

Each actor instance uses a **segmented queue architecture** where large queues are split into fixed-size segments (default: 100 items). This prevents memory/network bottlenecks when queues grow large.

State is stored as: `queue_0_seg_0`, `queue_0_seg_1`, `queue_1_seg_0`, etc., plus a `metadata` map with segment pointers. Pop operations drain from priority 0 completely before moving to priority 1, and so on.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for technical details.

## Performance

Given 40 virtual users, each making 10,000 operations. The avg latency is around 10ms. This is 3 worker containers (hosting actors) + 1 gateway container for the API server.

![Alt text of the image](https://github.com/olitomlinson/dapr-mq/blob/main/dotnet/tests/DaprMQ.PerformanceTests/results/performance-results_2026-03-08_22-15-12.png)

**Time Complexity:**
- `Push`: O(1) amortized append operation
- `Pop()`: O(k) where k = number of priority levels with items
  - Scans priorities 0 → N to find first item (O(k))
  - Removes 1 item from front of segment (O(1))

**Space Complexity:**
- O(n) where n = total items in queue
- **Segmented storage** (v4.0+): Max 100 items loaded per operation (vs entire queue)

**I/O Operations:**
- Push: 2 state store operations (segment + metadata)
- Pop: 2 state store operations (segment + metadata)

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for optimization strategies.

## Documentation

- **[Quick Start Guide](docs/QUICKSTART.md)** - Get running in minutes
- **[API Reference](docs/API_REFERENCE.md)** - Complete method documentation
- **[Architecture](docs/ARCHITECTURE.md)** - How it works under the hood
- **[Examples](examples/)** - Code samples and patterns

## Requirements

- Dapr 1.17.0+
- A Dapr-supported state store (PostgreSQL, Redis, Cosmos DB, etc.)
- .NET 10.0+ (if using the Dapr Actor SDK for direct access)

## Contributing

Contributions welcome! Please fork the repository, create a feature branch, add tests, and submit a pull request.

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines (if available), or just open an issue to discuss your ideas.

## 📄 Licensing

daprMQ is distributed under the Elastic License 2.0 (ELv2) with additional limitations.

### ✅ What you can do for free

You are free to use daprMQ:

- In development and testing environments  
- In **production for internal use**  
- Within your organisation without restriction  
- Modify the source code for internal purposes  

### ❌ What requires a commercial license

You must obtain a commercial license if you:

- Offer daprMQ as a **hosted or managed service (SaaS)**  
- Sell or distribute daprMQ as part of a **commercial product**  
- Build a **competing product or service** using daprMQ  
- Embed daprMQ as a **core feature** in a commercial offering  

### 🤝 Commercial Licensing

If your use case falls outside the free usage terms, a commercial license is required.

📧 Contact: oliverjamestomlinson@gmail.com

### 🧾 Summary

| Use Case | अनुमति |
|----------|--------|
| Internal use (including production) | ✅ Free |
| SaaS / hosted offering | ❌ Requires license |
| Competing product | ❌ Requires license |
| Redistribution | ⚠️ Allowed with restrictions |

For full details, see the [LICENSE](./LICENSE.md) file.

## Support

- **Issues**: [GitHub Issue Tracker](https://github.com/olitomlinson/dapr-mq/issues)
- **Docs**: [docs.dapr.io](https://docs.dapr.io/)
