package integration

import (
	"context"
	"encoding/json"
	"slices"
	"testing"

	daprmq "github.com/olitomlinson/dapr-mq/sdks/go"
)

func seqs(t *testing.T, items []daprmq.DequeueLockedItem) []int {
	t.Helper()
	out := make([]int, len(items))
	for i, item := range items {
		var v struct {
			Seq int `json:"seq"`
		}
		if err := json.Unmarshal(item.Item, &v); err != nil {
			t.Fatal(err)
		}
		out[i] = v.Seq
	}
	return out
}

func TestQ01_Enqueue_DequeueLocked_Ack_Round_Trips(t *testing.T) {
	client := newClient(t, nil)
	ctx := context.Background()
	queueID := newQueueID()

	if _, err := client.Enqueue(ctx, queueID, []daprmq.EnqueueItem{{Item: map[string]int{"seq": 1}}}, nil); err != nil {
		t.Fatal(err)
	}
	result, err := client.DequeueLocked(ctx, queueID, nil)
	if err != nil {
		t.Fatal(err)
	}
	if got := seqs(t, result.Items); !slices.Equal(got, []int{1}) || result.Items[0].LockID == "" || result.Items[0].LockExpiresAt.IsZero() {
		t.Fatalf("result = %+v", result)
	}
	if err := client.Acknowledge(ctx, queueID, result.Items[0].LockID, nil); err != nil {
		t.Fatal(err)
	}

	empty, err := client.DequeueLocked(ctx, queueID, nil)
	if err != nil || len(empty.Items) != 0 {
		t.Fatalf("the acknowledged item is gone: %+v %v", empty, err)
	}
}

func TestQ06_Dequeue_On_Empty_Queue_Returns_No_Items_And_No_Error(t *testing.T) {
	result, err := newClient(t, nil).DequeueLocked(context.Background(), newQueueID(), nil)

	if err != nil || len(result.Items) != 0 || result.Locked {
		t.Fatalf("result=%+v err=%v", result, err)
	}
}

func TestB01_AcknowledgeBatch_Settles_Every_Lock_From_One_Bulk_Dequeue(t *testing.T) {
	client := newClient(t, nil)
	ctx := context.Background()
	queueID := newQueueID()
	var items []daprmq.EnqueueItem
	for seq := 1; seq <= 5; seq++ {
		items = append(items, daprmq.EnqueueItem{Item: map[string]int{"seq": seq}})
	}
	if _, err := client.Enqueue(ctx, queueID, items, nil); err != nil {
		t.Fatal(err)
	}

	locked, err := client.DequeueLocked(ctx, queueID, &daprmq.DequeueLockedOptions{Count: 5})
	if err != nil {
		t.Fatal(err)
	}
	if got := seqs(t, locked.Items); !slices.Equal(got, []int{1, 2, 3, 4, 5}) {
		t.Fatalf("dequeued %v", got)
	}
	var lockIDs []string
	for _, item := range locked.Items {
		lockIDs = append(lockIDs, item.LockID)
	}

	result, err := client.AcknowledgeBatch(ctx, queueID, lockIDs, nil)
	if err != nil {
		t.Fatal(err)
	}

	if result.ItemsAcknowledged != 5 || len(result.Results) != 5 {
		t.Fatalf("result = %+v", result)
	}
	for i, r := range result.Results {
		if r.LockID != lockIDs[i] || r.Outcome != daprmq.AcknowledgeOutcomeAcknowledged {
			t.Fatalf("result[%d] = %+v", i, r)
		}
	}
	if empty, err := client.DequeueLocked(ctx, queueID, nil); err != nil || len(empty.Items) != 0 {
		t.Fatalf("nothing is redelivered: %+v %v", empty, err)
	}
}

func TestS01_Session_Items_Are_Served_In_Order_Under_A_Targeted_Lease(t *testing.T) {
	client := newClient(t, nil)
	ctx := context.Background()
	queueID := newQueueID()
	for seq := 1; seq <= 3; seq++ {
		if _, err := client.Enqueue(ctx, queueID, []daprmq.EnqueueItem{{Item: map[string]int{"seq": seq}, SessionID: "order-42"}}, nil); err != nil {
			t.Fatal(err)
		}
	}

	lease, err := client.AcceptSession(ctx, queueID, &daprmq.AcceptSessionOptions{SessionID: "order-42"})
	if err != nil || lease == nil || lease.SessionID != "order-42" || lease.LeaseID == "" {
		t.Fatalf("lease=%+v err=%v", lease, err)
	}
	sessionQueue := queueID + "-session-" + lease.SessionID

	var got []int
	for range 3 {
		result, err := client.DequeueLocked(ctx, sessionQueue, &daprmq.DequeueLockedOptions{LeaseID: lease.LeaseID})
		if err != nil {
			t.Fatal(err)
		}
		got = append(got, seqs(t, result.Items)...)
		for _, item := range result.Items {
			if err := client.Acknowledge(ctx, sessionQueue, item.LockID, &daprmq.LockOptions{LeaseID: lease.LeaseID}); err != nil {
				t.Fatal(err)
			}
		}
	}
	if !slices.Equal(got, []int{1, 2, 3}) {
		t.Fatalf("got %v", got)
	}

	if _, err := client.RenewSessionLease(ctx, queueID, lease.SessionID, lease.LeaseID, nil); err != nil {
		t.Fatal(err)
	}
	if err := client.ReleaseSession(ctx, queueID, lease.SessionID, lease.LeaseID); err != nil {
		t.Fatal(err)
	}
}
