CREATE TABLE u9_material_sync_batch (
    id BINARY(16) NOT NULL PRIMARY KEY,
    status VARCHAR(32) NOT NULL,
    requested_by VARCHAR(100) NOT NULL,
    requested_role VARCHAR(64) NOT NULL,
    total_count INT NOT NULL,
    completed_count INT NOT NULL DEFAULT 0,
    succeeded_count INT NOT NULL DEFAULT 0,
    waiting_count INT NOT NULL DEFAULT 0,
    failed_count INT NOT NULL DEFAULT 0,
    current_task_id BINARY(16) NULL,
    current_material_code VARCHAR(160) NULL,
    last_error VARCHAR(2000) NULL,
    created_at DATETIME(6) NOT NULL,
    started_at DATETIME(6) NULL,
    completed_at DATETIME(6) NULL,
    KEY ix_u9_material_sync_batch_actor (requested_by,created_at),
    KEY ix_u9_material_sync_batch_status (status,created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE u9_material_sync_batch_item (
    id BINARY(16) NOT NULL PRIMARY KEY,
    batch_id BINARY(16) NOT NULL,
    task_id BINARY(16) NOT NULL,
    ordinal_no INT NOT NULL,
    status VARCHAR(32) NOT NULL,
    message VARCHAR(2000) NULL,
    started_at DATETIME(6) NULL,
    completed_at DATETIME(6) NULL,
    lease_expires_at DATETIME(6) NULL,
    CONSTRAINT fk_u9_material_sync_batch_item_batch FOREIGN KEY (batch_id) REFERENCES u9_material_sync_batch(id) ON DELETE CASCADE,
    CONSTRAINT fk_u9_material_sync_batch_item_task FOREIGN KEY (task_id) REFERENCES u9_material_sync_task(id),
    UNIQUE KEY ux_u9_material_sync_batch_item_order (batch_id,ordinal_no),
    UNIQUE KEY ux_u9_material_sync_batch_item_task (batch_id,task_id),
    KEY ix_u9_material_sync_batch_item_claim (status,lease_expires_at,batch_id,ordinal_no)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
