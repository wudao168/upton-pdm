CREATE TABLE IF NOT EXISTS project_plan_template (
    id BINARY(16) NOT NULL PRIMARY KEY,
    name VARCHAR(120) NOT NULL,
    project_type_code VARCHAR(64) NULL,
    is_active TINYINT(1) NOT NULL DEFAULT 1,
    payload_json LONGTEXT NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    row_version BIGINT NOT NULL DEFAULT 1,
    INDEX ix_project_plan_template_active_type(is_active, project_type_code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS project_plan (
    id BINARY(16) NOT NULL PRIMARY KEY,
    project_id BINARY(16) NOT NULL,
    template_id BINARY(16) NOT NULL,
    current_stage VARCHAR(40) NOT NULL,
    planned_start DATE NOT NULL,
    planned_finish DATE NOT NULL,
    forecast_finish DATE NOT NULL,
    payload_json LONGTEXT NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    row_version BIGINT NOT NULL DEFAULT 1,
    UNIQUE KEY ux_project_plan_project(project_id),
    INDEX ix_project_plan_due(forecast_finish, current_stage),
    CONSTRAINT fk_project_plan_project FOREIGN KEY(project_id) REFERENCES project(id),
    CONSTRAINT fk_project_plan_template FOREIGN KEY(template_id) REFERENCES project_plan_template(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS project_plan_version (
    id BINARY(16) NOT NULL PRIMARY KEY,
    plan_id BINARY(16) NOT NULL,
    version_number INT NOT NULL,
    change_reason VARCHAR(300) NOT NULL,
    snapshot_json LONGTEXT NOT NULL,
    created_by VARCHAR(120) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY ux_project_plan_version(plan_id, version_number),
    CONSTRAINT fk_project_plan_version_plan FOREIGN KEY(plan_id) REFERENCES project_plan(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
