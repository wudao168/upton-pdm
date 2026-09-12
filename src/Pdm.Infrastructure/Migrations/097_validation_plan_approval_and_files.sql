ALTER TABLE project_validation_plan
    DROP INDEX ux_project_validation_plan_project,
    ADD COLUMN revision_number INT NOT NULL DEFAULT 1 AFTER project_id,
    ADD COLUMN state VARCHAR(30) NOT NULL DEFAULT 'Draft' AFTER revision_number,
    ADD COLUMN workflow_code VARCHAR(80) NULL AFTER validation_date,
    ADD COLUMN workflow_version INT NULL AFTER workflow_code,
    ADD COLUMN submitted_by VARCHAR(100) NULL AFTER workflow_version,
    ADD COLUMN submitted_at DATETIME(6) NULL AFTER submitted_by,
    ADD COLUMN effective_by VARCHAR(100) NULL AFTER submitted_at,
    ADD COLUMN effective_at DATETIME(6) NULL AFTER effective_by,
    ADD UNIQUE KEY ux_project_validation_plan_revision (project_id,revision_number),
    ADD KEY ix_project_validation_plan_current (project_id,revision_number,state);

CREATE TABLE IF NOT EXISTS validation_plan_approval_task (
    id BINARY(16) NOT NULL PRIMARY KEY,
    plan_id BINARY(16) NOT NULL,
    step_order INT NOT NULL,
    stage VARCHAR(50) NOT NULL,
    step_name VARCHAR(120) NOT NULL,
    assignee VARCHAR(100) NOT NULL,
    decision VARCHAR(30) NULL,
    decision_by VARCHAR(100) NULL,
    decision_comment VARCHAR(500) NULL,
    created_at DATETIME(6) NOT NULL,
    decided_at DATETIME(6) NULL,
    CONSTRAINT fk_validation_plan_approval_task_plan FOREIGN KEY (plan_id) REFERENCES project_validation_plan(id),
    UNIQUE KEY ux_validation_plan_approval_step (plan_id,step_order),
    KEY ix_validation_plan_approval_assignee (assignee,decision,created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS validation_plan_attachment (
    id BINARY(16) NOT NULL PRIMARY KEY,
    plan_id BINARY(16) NOT NULL,
    attachment_kind VARCHAR(30) NOT NULL,
    original_file_name VARCHAR(255) NOT NULL,
    file_version INT NOT NULL,
    storage_relative_path VARCHAR(1000) NOT NULL,
    file_length BIGINT NOT NULL,
    sha256 CHAR(64) NOT NULL,
    uploaded_by VARCHAR(100) NOT NULL,
    uploaded_at DATETIME(6) NOT NULL,
    CONSTRAINT fk_validation_plan_attachment_plan FOREIGN KEY (plan_id) REFERENCES project_validation_plan(id),
    UNIQUE KEY ux_validation_plan_attachment_version (plan_id,attachment_kind,original_file_name,file_version),
    KEY ix_validation_plan_attachment_plan (plan_id,attachment_kind,uploaded_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
