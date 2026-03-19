CloudPool pool = await client.PoolOperations.GetPoolAsync(
    this._settings.BatchPoolId,
    detailLevel: new ODATADetailLevel(selectClause: "id,allocationState,autoScaleFormula,autoScaleRun"),
    additionalBehaviors: BatchClientRetryPolicies);

// Only proceed once the previous evaluation has fully propagated (5-minute orchestrator window).
// The distributed lock ensures only one instance reaches this point concurrently, so the
// rare propagation-delay errors are limited to the window between lock release and pool state sync.
bool passedCooldown = pool.AutoScaleRun?.Timestamp is not { } lastRun
                      || DateTime.UtcNow - lastRun.ToUniversalTime() >= TimeSpan.FromMinutes(5);

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
}
