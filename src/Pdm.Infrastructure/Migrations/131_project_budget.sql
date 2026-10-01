CREATE TABLE project_budget (
    project_id BINARY(16) NOT NULL PRIMARY KEY,
    row_version BIGINT NOT NULL,
    payload_json JSON NOT NULL,
    updated_by VARCHAR(100) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    CONSTRAINT fk_project_budget_project FOREIGN KEY (project_id) REFERENCES project(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE project_budget_version (
    project_id BINARY(16) NOT NULL,
    row_version BIGINT NOT NULL,
    payload_json JSON NOT NULL,
    updated_by VARCHAR(100) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY(project_id,row_version),
    CONSTRAINT fk_project_budget_version_project FOREIGN KEY (project_id) REFERENCES project(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'project.budget.view',UTC_TIMESTAMP(6) FROM role_definition
WHERE role_code IN ('PlanningManager','BusinessUnitManager','MechanicalManager','ProjectManager','SupplyChain','ProcurementSpecialist','ProcurementManager','ProductionManager','MachiningSupervisor','AssemblySupervisor','ElectricalSupervisor','Administrator','developer');
INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'project.budget.edit',UTC_TIMESTAMP(6) FROM role_definition WHERE role_code IN ('PlanningManager','Administrator','developer');

ALTER TABLE u9_procurement_snapshot ADD COLUMN order_net_amount DECIMAL(24,2) NULL;

UPDATE pdm_system_setting SET setting_value=JSON_ARRAY_APPEND(setting_value,'$.rules.MainDesigner','project.budget.view')
WHERE setting_key='project_permission_settings' AND JSON_CONTAINS_PATH(setting_value,'one','$.rules.MainDesigner')
AND NOT JSON_CONTAINS(JSON_EXTRACT(setting_value,'$.rules.MainDesigner'),JSON_QUOTE('project.budget.view'));
