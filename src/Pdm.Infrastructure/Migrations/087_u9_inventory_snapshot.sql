CREATE TABLE u9_inventory_sync_setting (
    id TINYINT UNSIGNED NOT NULL PRIMARY KEY,
    auto_sync_enabled TINYINT(1) NOT NULL DEFAULT 1,
    sync_interval_minutes INT NOT NULL DEFAULT 60,
    query_path VARCHAR(500) NOT NULL DEFAULT '/webapi/Invtrans/QueryQohAndAvailable',
    current_snapshot_run_id BINARY(16) NULL,
    updated_by VARCHAR(100) NULL,
    updated_at DATETIME(6) NULL,
    CONSTRAINT ck_u9_inventory_sync_setting_singleton CHECK (id=1),
    CONSTRAINT ck_u9_inventory_sync_interval CHECK (sync_interval_minutes BETWEEN 15 AND 1440)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO u9_inventory_sync_setting(id,auto_sync_enabled,sync_interval_minutes,query_path)
VALUES(1,1,60,'/webapi/Invtrans/QueryQohAndAvailable');

CREATE TABLE u9_inventory_sync_run (
    id BINARY(16) NOT NULL PRIMARY KEY,
    trigger_kind VARCHAR(32) NOT NULL,
    status VARCHAR(32) NOT NULL,
    source_row_count INT NOT NULL DEFAULT 0,
    stored_row_count INT NOT NULL DEFAULT 0,
    material_count INT NOT NULL DEFAULT 0,
    last_error TEXT NULL,
    started_at DATETIME(6) NOT NULL,
    completed_at DATETIME(6) NULL,
    KEY ix_u9_inventory_sync_run_started_at (started_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE u9_inventory_snapshot (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    snapshot_run_id BINARY(16) NOT NULL,
    organization_code VARCHAR(50) NOT NULL,
    warehouse_code VARCHAR(80) NOT NULL,
    warehouse_name VARCHAR(200) NOT NULL,
    material_code VARCHAR(160) NOT NULL,
    item_name VARCHAR(300) NOT NULL,
    specification VARCHAR(500) NULL,
    project_code VARCHAR(160) NULL,
    project_name VARCHAR(300) NULL,
    subproject VARCHAR(160) NULL,
    stock_quantity DECIMAL(24,9) NOT NULL DEFAULT 0,
    available_quantity DECIMAL(24,9) NOT NULL DEFAULT 0,
    reserved_quantity DECIMAL(24,9) NOT NULL DEFAULT 0,
    unavailable_quantity DECIMAL(24,9) NOT NULL DEFAULT 0,
    bin_code VARCHAR(80) NULL,
    bin_name VARCHAR(200) NULL,
    storage_type VARCHAR(100) NULL,
    refreshed_at DATETIME(6) NOT NULL,
    KEY ix_u9_inventory_snapshot_run_material (snapshot_run_id,material_code),
    KEY ix_u9_inventory_snapshot_run_warehouse (snapshot_run_id,warehouse_code),
    KEY ix_u9_inventory_snapshot_refreshed_at (refreshed_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
