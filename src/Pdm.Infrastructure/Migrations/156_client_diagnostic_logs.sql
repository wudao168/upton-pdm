CREATE TABLE IF NOT EXISTS client_diagnostic_logs (
    id CHAR(64) NOT NULL PRIMARY KEY,
    company_id CHAR(36) NOT NULL,
    username VARCHAR(160) NOT NULL,
    batch_id CHAR(32) NOT NULL,
    device_id CHAR(32) NOT NULL,
    machine VARCHAR(160) NOT NULL,
    component VARCHAR(32) NOT NULL,
    kind VARCHAR(16) NOT NULL,
    version VARCHAR(160) NOT NULL,
    ui_version VARCHAR(160) NOT NULL,
    started_at DATETIME(6) NOT NULL,
    ended_at DATETIME(6) NOT NULL,
    received_at DATETIME(6) NOT NULL,
    entry_count INT NOT NULL,
    bytes BIGINT NOT NULL,
    INDEX ix_client_logs_company_time (company_id, received_at),
    INDEX ix_client_logs_device_time (company_id, device_id, started_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
