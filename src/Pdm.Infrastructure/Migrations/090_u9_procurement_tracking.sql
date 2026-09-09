CREATE TABLE u9_procurement_sync_setting (
    id TINYINT UNSIGNED NOT NULL PRIMARY KEY,
    auto_sync_enabled TINYINT(1) NOT NULL DEFAULT 1,
    sync_interval_minutes INT NOT NULL DEFAULT 15,
    query_path VARCHAR(500) NOT NULL DEFAULT '/webapi/QueryCommon/QueryInfoBySql',
    current_snapshot_run_id BINARY(16) NULL,
    updated_by VARCHAR(100) NULL,
    updated_at DATETIME(6) NULL,
    CONSTRAINT ck_u9_procurement_sync_setting_singleton CHECK (id=1),
    CONSTRAINT ck_u9_procurement_sync_interval CHECK (sync_interval_minutes BETWEEN 15 AND 1440)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO u9_procurement_sync_setting(id,auto_sync_enabled,sync_interval_minutes,query_path)
VALUES(1,1,15,'/webapi/QueryCommon/QueryInfoBySql');

CREATE TABLE u9_procurement_sync_run (
    id BINARY(16) NOT NULL PRIMARY KEY,
    trigger_kind VARCHAR(32) NOT NULL,
    status VARCHAR(32) NOT NULL,
    source_row_count INT NOT NULL DEFAULT 0,
    stored_row_count INT NOT NULL DEFAULT 0,
    project_count INT NOT NULL DEFAULT 0,
    last_error TEXT NULL,
    started_at DATETIME(6) NOT NULL,
    completed_at DATETIME(6) NULL,
    KEY ix_u9_procurement_sync_run_started_at (started_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE u9_procurement_snapshot (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    snapshot_run_id BINARY(16) NOT NULL,
    organization_code VARCHAR(50) NOT NULL,
    record_kind VARCHAR(8) NOT NULL,
    line_id VARCHAR(80) NOT NULL,
    source_pr_line_id VARCHAR(80) NULL,
    document_number VARCHAR(100) NOT NULL,
    line_number INT NOT NULL,
    line_status INT NOT NULL,
    is_canceled TINYINT(1) NOT NULL DEFAULT 0,
    business_date DATETIME(6) NULL,
    material_code VARCHAR(160) NOT NULL,
    item_name VARCHAR(300) NOT NULL,
    specification VARCHAR(500) NULL,
    brand VARCHAR(200) NULL,
    project_code VARCHAR(160) NULL,
    project_name VARCHAR(300) NULL,
    subproject VARCHAR(160) NULL,
    requested_quantity DECIMAL(24,9) NOT NULL DEFAULT 0,
    approved_quantity DECIMAL(24,9) NOT NULL DEFAULT 0,
    purchase_quantity DECIMAL(24,9) NOT NULL DEFAULT 0,
    arrived_quantity DECIMAL(24,9) NOT NULL DEFAULT 0,
    purchase_remark VARCHAR(1000) NULL,
    delivery_date DATETIME(6) NULL,
    latest_delivery_date DATETIME(6) NULL,
    refreshed_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_u9_procurement_snapshot_line (snapshot_run_id,record_kind,line_id),
    KEY ix_u9_procurement_snapshot_project (snapshot_run_id,project_code,subproject),
    KEY ix_u9_procurement_snapshot_material (snapshot_run_id,material_code),
    KEY ix_u9_procurement_snapshot_source_pr (snapshot_run_id,source_pr_line_id),
    KEY ix_u9_procurement_snapshot_refreshed_at (refreshed_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
