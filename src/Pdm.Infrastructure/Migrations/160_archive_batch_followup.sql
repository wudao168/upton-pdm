CREATE TABLE IF NOT EXISTS archive_batch_followup (
    batch_id BINARY(16) NOT NULL PRIMARY KEY,
    project_id BINARY(16) NOT NULL,
    followup_json JSON NOT NULL,
    next_attempt_at DATETIME(6) NOT NULL,
    KEY ix_archive_followup_due (next_attempt_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
