CREATE TABLE IF NOT EXISTS production_scan_catalog (
    company_id BINARY(16) NOT NULL PRIMARY KEY,
    payload_json JSON NOT NULL,
    row_version BIGINT NOT NULL DEFAULT 1
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'production.scan.manage',UTC_TIMESTAMP(6) FROM role_definition
WHERE role_code='ProductionManager';
