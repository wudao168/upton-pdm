ALTER TABLE crm_integration_setting
    ADD COLUMN auto_sync_enabled TINYINT(1) NOT NULL DEFAULT 0 AFTER password_ciphertext,
    ADD COLUMN auto_sync_interval_minutes INT NOT NULL DEFAULT 60 AFTER auto_sync_enabled,
    ADD COLUMN last_auto_sync_attempt_at DATETIME(6) NULL AFTER last_sync_count,
    ADD COLUMN last_auto_sync_error VARCHAR(1000) NULL AFTER last_auto_sync_attempt_at;
