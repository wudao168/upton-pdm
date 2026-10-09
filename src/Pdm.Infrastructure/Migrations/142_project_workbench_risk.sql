CREATE TABLE IF NOT EXISTS project_workbench_risk (
    id BINARY(16) NOT NULL PRIMARY KEY,
    project_id BINARY(16) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    payload_json JSON NOT NULL,
    INDEX ix_project_workbench_risk_project (project_id)
);
