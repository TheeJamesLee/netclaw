## MODIFIED Requirements

### Requirement: Detector evidence retains its live task lifetime

The system SHALL retain established recurrence and unresolved corrections through live task continuation and compaction.
It SHALL NOT persist recurrence detector state for crash recovery.
Recovery SHALL preserve durable child results, job lineage, input closure, and assistant reports without replaying external effects.
It SHALL never infer trusted success from result text or an absent receipt.
A final actual result without its mandatory receipt SHALL cause an explicit runtime contract failure.
The system SHALL preserve known results and prior evidence, complete required call-result pairs, and settle partial/failure without another model request.
In-flight calls, approval waits, and external cancellation SHALL retain their separate lifecycle contracts.
Fresh authorized user input or a fresh scheduled task SHALL reset live task evidence.
Overflow replay, result admission, and approval retries SHALL NOT perform that reset while the session remains active.
The first durable consumption of a canonical child result SHALL start a fresh in-memory recurrence window for that continuation.
The owner SHALL retain original authority and durable delivery identity.
It SHALL NOT store parent detector checkpoints, receipt-failure facts, or same-task detector copies in child or job ledgers.
Acceptance, status, terminal preparation, admission, recovery, and duplicate adoption SHALL NOT grant this exception.
Shell-job result continuation and legacy child adoption events SHALL NOT restore durable detector checkpoints.

The recent cold-key horizon SHALL contain at most 256 keys with one observation.
The system SHALL retain pinned suspicion outside the canonical child-window exception.
A changed actual outcome or a fresh authorized task SHALL invalidate that suspicion.
It SHALL NOT evict pinned evidence through age, cold-key pressure, compaction, or a status query.
It SHALL surface resource and persistence failures without silently discarding evidence.
Usage count alone SHALL NOT terminate a useful task.

#### Scenario: Compaction retains an unresolved correction

- **GIVEN** the model received an exact recurrence correction
- **WHEN** the session compacts and the model repeats that identity
- **THEN** the runtime reaches terminal settlement

#### Scenario: Recovery preserves committed results without detector state

- **GIVEN** an interrupted authorized task has committed tool results
- **WHEN** the parent recovers and resumes from those records
- **THEN** recovery does not redispatch calls with committed results
- **AND** the detector starts from live observations after restart

#### Scenario: A new scheduled task starts cleanly

- **GIVEN** a previous task produced a repeated status result
- **WHEN** a distinct authorized schedule occurrence starts a new task
- **THEN** the previous task's detector state does not penalize the new task

#### Scenario: Cold-key pressure cannot erase established suspicion

- **GIVEN** an identity has two equal completed observations and a correction
- **WHEN** the task produces more than 256 other distinct identities
- **THEN** the unresolved correction remains effective

#### Scenario: A missing receipt is not success

- **GIVEN** a correlated actual result lacks its trusted receipt
- **WHEN** the group completes
- **THEN** the runtime reports a missing-receipt contract failure
- **AND** it preserves result pairs and prior evidence without a fabricated success
- **AND** it settles a truthful partial/failure result without another model request

#### Scenario: Useful work exceeds the former parent ceiling

- **GIVEN** a deterministic task requires more than 60 distinct useful tool rounds
- **WHEN** the parent executes the task
- **THEN** no iteration count stops the task
- **AND** independent artifact checks prove its required result
