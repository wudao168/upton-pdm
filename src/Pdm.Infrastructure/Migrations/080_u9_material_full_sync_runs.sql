CREATE TABLE u9_material_full_sync_run (
    id CHAR(36) NOT NULL,
    trigger_kind VARCHAR(32) NOT NULL,
    status VARCHAR(32) NOT NULL,
    category_codes_json LONGTEXT NOT NULL,
    category_results_json LONGTEXT NOT NULL,
    category_count INT NOT NULL DEFAULT 0,
    completed_category_count INT NOT NULL DEFAULT 0,
    discovered_count INT NOT NULL DEFAULT 0,
    created_count INT NOT NULL DEFAULT 0,
    refreshed_count INT NOT NULL DEFAULT 0,
    skipped_count INT NOT NULL DEFAULT 0,
    failed_category_count INT NOT NULL DEFAULT 0,
    last_error TEXT NULL,
    started_at DATETIME(6) NOT NULL,
    completed_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    KEY ix_u9_material_full_sync_run_started_at (started_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
