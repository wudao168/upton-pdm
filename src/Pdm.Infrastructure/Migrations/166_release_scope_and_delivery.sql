SET @pdm_sql = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='release_package' AND column_name='confirmed_removed_bom_item_ids_json'),
    'SELECT 1', 'ALTER TABLE release_package ADD COLUMN confirmed_removed_bom_item_ids_json JSON NULL');
PREPARE pdm_stmt FROM @pdm_sql;
EXECUTE pdm_stmt;
DEALLOCATE PREPARE pdm_stmt;
SET @pdm_sql = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='release_package' AND column_name='version_bindings_json'),
    'SELECT 1', 'ALTER TABLE release_package ADD COLUMN version_bindings_json JSON NULL');
PREPARE pdm_stmt FROM @pdm_sql;
EXECUTE pdm_stmt;
DEALLOCATE PREPARE pdm_stmt;

CREATE TABLE IF NOT EXISTS release_notification_configuration (
    company_id BINARY(16) NOT NULL PRIMARY KEY,
    protected_payload LONGTEXT NOT NULL,
    enabled BOOLEAN NOT NULL DEFAULT FALSE,
    enabled_at DATETIME(6) NULL,
    row_version BIGINT NOT NULL DEFAULT 0
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS release_delivery_state (
    release_package_id BINARY(16) NOT NULL PRIMARY KEY,
    company_id BINARY(16) NOT NULL,
    project_id BINARY(16) NOT NULL,
    payload_json JSON NOT NULL,
    row_version BIGINT NOT NULL DEFAULT 1,
    next_check_at DATETIME(6) NULL,
    lease_id BINARY(16) NULL,
    lease_until DATETIME(6) NULL,
    INDEX ix_release_delivery_due (next_check_at,lease_until),
    INDEX ix_release_delivery_project (project_id,company_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
