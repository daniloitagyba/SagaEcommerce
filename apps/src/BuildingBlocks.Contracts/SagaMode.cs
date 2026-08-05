namespace BuildingBlocks;

// Milestone 22 originally compared choreography against orchestration with
// one side (choreography: Orders<->Payments, with outbox/inbox/DLQ) far
// more hardened than the other (orchestration's payment-decision step:
// stateless, no persistence, no DLQ) - not a fair comparison of the two
// patterns, just hardened-vs-not. SagaMode makes the comparison legitimate:
// both paths now carry the same reliability guarantees, and this toggle
// picks which one is actually driving a given deployment (or both, to
// compare them side by side against the same traffic).
public enum SagaMode
{
    Choreography,
    Orchestration,
    Both
}
