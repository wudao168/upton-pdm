ALTER TABLE document
    ADD COLUMN checkout_session_id BINARY(16) NULL AFTER checked_out_at,
    ADD COLUMN checkout_machine VARCHAR(160) NULL AFTER checkout_session_id,
    ADD COLUMN checkout_last_heartbeat_at DATETIME(6) NULL AFTER checkout_machine,
    ADD COLUMN checkout_lease_expires_at DATETIME(6) NULL AFTER checkout_last_heartbeat_at,
    ADD COLUMN checkout_release_requested_by VARCHAR(100) NULL AFTER checkout_lease_expires_at,
    ADD COLUMN checkout_release_requested_at DATETIME(6) NULL AFTER checkout_release_requested_by,
    ADD COLUMN checkout_release_request_reason VARCHAR(500) NULL AFTER checkout_release_requested_at,
    ADD KEY ix_document_checkout_session (checkout_session_id),
    ADD KEY ix_document_checkout_heartbeat (checkout_last_heartbeat_at);

UPDATE document
SET checkout_last_heartbeat_at = checked_out_at,
    checkout_lease_expires_at = DATE_ADD(checked_out_at, INTERVAL 15 MINUTE)
WHERE checked_out_by IS NOT NULL AND checked_out_at IS NOT NULL;

INSERT IGNORE INTO pdm_system_setting(setting_key,setting_value,updated_at) VALUES
('checkout_heartbeat_seconds','180',UTC_TIMESTAMP(6)),
('checkout_lease_minutes','15',UTC_TIMESTAMP(6)),
('checkout_offline_grace_minutes','60',UTC_TIMESTAMP(6)),
('checkout_reminder_hours','4',UTC_TIMESTAMP(6)),
('checkout_strong_reminder_hours','8',UTC_TIMESTAMP(6)),
('checkout_overdue_hours','24',UTC_TIMESTAMP(6)),
('checkout_force_release_hours','48',UTC_TIMESTAMP(6));

INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at) VALUES
('Engineer','document.lock.request-release',UTC_TIMESTAMP(6)),
('Engineer','document.lock.force-release',UTC_TIMESTAMP(6)),
('Administrator','document.lock.request-release',UTC_TIMESTAMP(6)),
('Administrator','document.lock.force-release',UTC_TIMESTAMP(6));
