CREATE TABLE IF NOT EXISTS production_tracking_activation (
    id TINYINT NOT NULL PRIMARY KEY,
    activated_at DATETIME(6) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO production_tracking_activation(id, activated_at) VALUES(1, UTC_TIMESTAMP(6));

CREATE TABLE IF NOT EXISTS production_tracking_state (
    release_package_id BINARY(16) NOT NULL PRIMARY KEY,
    project_id BINARY(16) NOT NULL,
    company_id BINARY(16) NOT NULL,
    payload_json JSON NOT NULL,
    row_version BIGINT NOT NULL DEFAULT 1,
    INDEX ix_production_tracking_project (project_id, company_id),
    INDEX ix_production_tracking_company (company_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS production_capacity_state (
    company_id BINARY(16) NOT NULL PRIMARY KEY,
    payload_json JSON NOT NULL,
    row_version BIGINT NOT NULL DEFAULT 1
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'production.assign',UTC_TIMESTAMP(6) FROM role_definition
WHERE role_code IN ('SupplyChain','ProcurementManager');
INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'production.execute',UTC_TIMESTAMP(6) FROM role_definition
WHERE role_code IN ('ProductionManager','ProductionAssistant','MachiningSupervisor','MachiningOperator');
INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'production.schedule',UTC_TIMESTAMP(6) FROM role_definition
WHERE role_code IN ('ProductionManager','ProductionAssistant','MachiningSupervisor');
INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'production.capacity.manage',UTC_TIMESTAMP(6) FROM role_definition
WHERE role_code='ProductionManager';
INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'production.inspect',UTC_TIMESTAMP(6) FROM role_definition
WHERE role_code IN ('QualityManager','QualityInspector');
INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'production.issue',UTC_TIMESTAMP(6) FROM role_definition
WHERE role_code IN ('QualityManager','QualityInspector','ProductionViewer','AssemblyFitter');
