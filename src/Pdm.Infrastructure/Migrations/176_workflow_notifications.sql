CREATE TABLE IF NOT EXISTS workflow_notification_catalog (
    company_id BINARY(16) NOT NULL PRIMARY KEY,
    payload_json JSON NOT NULL,
    row_version BIGINT NOT NULL DEFAULT 1
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS workflow_notification_job (
    id BINARY(16) NOT NULL PRIMARY KEY,
    company_id BINARY(16) NOT NULL,
    source_key VARCHAR(180) NOT NULL,
    flow_code VARCHAR(64) NOT NULL,
    state VARCHAR(24) NOT NULL,
    next_check_at DATETIME(6) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    payload_json JSON NOT NULL,
    row_version BIGINT NOT NULL DEFAULT 1,
    UNIQUE KEY uq_workflow_notice_source (company_id,source_key),
    KEY ix_workflow_notice_due (state,next_check_at),
    KEY ix_workflow_notice_company (company_id,flow_code,created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
