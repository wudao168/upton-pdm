ALTER TABLE pdm_customer
    ADD COLUMN source_system VARCHAR(20) NOT NULL DEFAULT 'legacy' AFTER is_active,
    ADD COLUMN last_synced_at DATETIME(6) NULL AFTER source_system,
    ADD INDEX ix_pdm_customer_source_active (source_system,is_active,code);

CREATE TABLE crm_integration_setting (
    id TINYINT UNSIGNED NOT NULL PRIMARY KEY,
    base_url VARCHAR(500) NOT NULL,
    username VARCHAR(100) NOT NULL,
    password_ciphertext TEXT NOT NULL,
    last_sync_at DATETIME(6) NULL,
    last_sync_count INT NOT NULL DEFAULT 0,
    updated_at DATETIME(6) NOT NULL,
    updated_by VARCHAR(100) NOT NULL,
    CONSTRAINT ck_crm_integration_singleton CHECK (id=1)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
