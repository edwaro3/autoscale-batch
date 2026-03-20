CloudPool pool = await client.PoolOperations.GetPoolAsync(
    this._settings.BatchPoolId,
    detailLevel: new ODATADetailLevel(selectClause: "id,allocationState,autoScaleFormula,autoScaleRun"),
    additionalBehaviors: BatchClientRetryPolicies);

// pool.AutoScaleRun.Timestamp reflects when Azure Batch's internal evaluation engine last
// ran the formula (15-minute cadence) — NOT when the orchestrator last called EnableAutoScaleAsync.
// These are decoupled, so using AutoScaleRun.Timestamp for the cooldown check can produce a false
// "safe to call" result within seconds of a recent write, causing a rate limit error.
//
// Instead, we read the orchestrator's own last-write timestamp from the distributed lock store
// (the same blob backing the lock). This is the authoritative record of when we last pushed a
// formula, independent of Azure Batch's eventual-consistency propagation delay.
DateTimeOffset? lastScaleWrite = await this._scaleLockStore.GetLastWriteTimeAsync(this._settings.BatchPoolId);

bool passedCooldown = lastScaleWrite is null
                      || DateTimeOffset.UtcNow - lastScaleWrite.Value >= TimeSpan.FromMinutes(5);

if (pool.AllocationState is AllocationState.Steady && passedCooldown)
{
    string formattedFormula = string.Format(this._scaleFormula, maxJobs);

    // Evaluation interval is kept at 15 minutes: the orchestrator drives scale-up via the
    // 5-minute window on every request, while the pool's own 15-minute self-evaluation
    // handles gradual scale-down during demand dips without triggering cold starts.
    await client.PoolOperations.EnableAutoScaleAsync(
        this._settings.BatchPoolId,
        formattedFormula,
        autoScaleEvaluationInterval: TimeSpan.FromMinutes(15),
        additionalBehaviors: BatchClientRetryPolicies);

    // Record the write time immediately after a successful call so subsequent requests
    // within the 5-minute window see an accurate timestamp, not a stale pool evaluation time.
    await this._scaleLockStore.SetLastWriteTimeAsync(this._settings.BatchPoolId, DateTimeOffset.UtcNow);
}
