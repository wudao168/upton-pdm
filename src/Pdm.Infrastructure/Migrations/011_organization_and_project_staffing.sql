ALTER TABLE project
    ADD COLUMN execution_unit_id BINARY(16) NULL AFTER parent_project_id;

CREATE TABLE IF NOT EXISTS organization_unit (
    id BINARY(16) NOT NULL PRIMARY KEY,
    organization_id BINARY(16) NOT NULL,
    parent_unit_id BINARY(16) NULL,
    code VARCHAR(40) NOT NULL,
    name VARCHAR(160) NOT NULL,
    kind VARCHAR(40) NOT NULL,
    is_active TINYINT(1) NOT NULL DEFAULT 1,
    sort_order INT NOT NULL DEFAULT 0,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY ux_organization_unit_code (organization_id,code),
    KEY ix_organization_unit_parent (parent_unit_id),
    CONSTRAINT fk_organization_unit_organization FOREIGN KEY (organization_id) REFERENCES project_organization(id),
    CONSTRAINT fk_organization_unit_parent FOREIGN KEY (parent_unit_id) REFERENCES organization_unit(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

ALTER TABLE project
    ADD CONSTRAINT fk_project_execution_unit FOREIGN KEY (execution_unit_id) REFERENCES organization_unit(id),
    ADD KEY ix_project_execution_unit (execution_unit_id);

CREATE TABLE IF NOT EXISTS organization_membership (
    unit_id BINARY(16) NOT NULL,
    username VARCHAR(100) NOT NULL,
    is_primary TINYINT(1) NOT NULL DEFAULT 0,
    assigned_at DATETIME(6) NOT NULL,
    PRIMARY KEY (unit_id,username),
    KEY ix_organization_membership_user (username,is_primary),
    CONSTRAINT fk_organization_membership_unit FOREIGN KEY (unit_id) REFERENCES organization_unit(id),
    CONSTRAINT fk_organization_membership_user FOREIGN KEY (username) REFERENCES pdm_user(username)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS organization_unit_manager (
    unit_id BINARY(16) NOT NULL,
    username VARCHAR(100) NOT NULL,
    is_primary TINYINT(1) NOT NULL DEFAULT 0,
    assigned_at DATETIME(6) NOT NULL,
    PRIMARY KEY (unit_id,username),
    CONSTRAINT fk_organization_unit_manager_unit FOREIGN KEY (unit_id) REFERENCES organization_unit(id),
    CONSTRAINT fk_organization_unit_manager_user FOREIGN KEY (username) REFERENCES pdm_user(username)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS project_assignment (
    project_id BINARY(16) NOT NULL,
    username VARCHAR(100) NOT NULL,
    assignment_type VARCHAR(50) NOT NULL,
    assigned_by VARCHAR(100) NOT NULL,
    assigned_at DATETIME(6) NOT NULL,
    PRIMARY KEY (project_id,username,assignment_type),
    KEY ix_project_assignment_user (username,assignment_type),
    CONSTRAINT fk_project_assignment_project FOREIGN KEY (project_id) REFERENCES project(id),
    CONSTRAINT fk_project_assignment_user FOREIGN KEY (username) REFERENCES pdm_user(username)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
