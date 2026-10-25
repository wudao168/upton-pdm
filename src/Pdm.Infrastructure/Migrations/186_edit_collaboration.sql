CREATE TABLE IF NOT EXISTS document_edit_handoff (
    document_id BINARY(16) NOT NULL PRIMARY KEY,
    project_id BINARY(16) NOT NULL,
    from_user VARCHAR(80) NOT NULL,
    to_user VARCHAR(80) NOT NULL,
    version_id BINARY(16) NOT NULL,
    expires_at DATETIME(6) NOT NULL,
    wait_source_key VARCHAR(200) NULL,
    INDEX ix_edit_handoff_expiry(expires_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- 保留升级前仍在等待的申请；新键区分同一进程会话内的多轮编辑。
UPDATE user_notification n JOIN document d
ON n.source_key=CONCAT('edit-wait:',LOWER(HEX(d.id)),':',LOWER(HEX(d.checkout_session_id)))
SET n.source_key=CONCAT(n.source_key,':',621355968000000000+TIMESTAMPDIFF(MICROSECOND,'1970-01-01',d.checkout_release_requested_at)*10)
WHERE d.checked_out_by IS NOT NULL AND d.checkout_release_requested_at IS NOT NULL
  AND n.created_at>=d.checked_out_at;

UPDATE pdm_system_setting SET setting_value='60',updated_at=UTC_TIMESTAMP(6)
WHERE setting_key='checkout_heartbeat_seconds' AND setting_value='180';
UPDATE pdm_system_setting SET setting_value='5',updated_at=UTC_TIMESTAMP(6)
WHERE setting_key='checkout_lease_minutes' AND setting_value='15';
UPDATE pdm_system_setting SET setting_value='30',updated_at=UTC_TIMESTAMP(6)
WHERE setting_key='checkout_offline_grace_minutes' AND setting_value='60';
