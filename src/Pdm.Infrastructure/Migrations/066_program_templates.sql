CREATE TABLE program_template_counter (
    asset_type VARCHAR(40) NOT NULL PRIMARY KEY,
    current_value INT UNSIGNED NOT NULL DEFAULT 0
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE program_template (
    id BINARY(16) NOT NULL PRIMARY KEY,
    code VARCHAR(30) NOT NULL,
    asset_type VARCHAR(40) NOT NULL,
    origin_company_id BINARY(16) NULL,
    current_published_revision_id BINARY(16) NULL,
    is_archived TINYINT(1) NOT NULL DEFAULT 0,
    created_by VARCHAR(100) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY ux_program_template_code (code),
    KEY ix_program_template_origin_company (origin_company_id),
    CONSTRAINT fk_program_template_origin_company FOREIGN KEY (origin_company_id) REFERENCES project_organization(id),
    CONSTRAINT ck_program_template_asset_type CHECK (asset_type IN ('PlcFunctionBlock','PlcProgram','HmiTemplate'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE program_template_revision (
    id BINARY(16) NOT NULL PRIMARY KEY,
    template_id BINARY(16) NOT NULL,
    version_major INT UNSIGNED NOT NULL,
    version_minor INT UNSIGNED NOT NULL,
    version_patch INT UNSIGNED NOT NULL,
    attempt_number INT UNSIGNED NOT NULL,
    state VARCHAR(40) NOT NULL,
    name VARCHAR(200) NOT NULL,
    category VARCHAR(100) NOT NULL DEFAULT '',
    description VARCHAR(2000) NOT NULL,
    vendor VARCHAR(100) NOT NULL,
    platform VARCHAR(100) NOT NULL,
    software_version VARCHAR(100) NOT NULL,
    applicable_series VARCHAR(300) NOT NULL DEFAULT '',
    tags_json JSON NOT NULL,
    change_note VARCHAR(1000) NOT NULL,
    package_file_name VARCHAR(500) NULL,
    package_storage_path VARCHAR(1000) NULL,
    package_file_length BIGINT NULL,
    package_sha256 CHAR(64) NULL,
    evidence_file_name VARCHAR(500) NULL,
    evidence_storage_path VARCHAR(1000) NULL,
    evidence_file_length BIGINT NULL,
    evidence_sha256 CHAR(64) NULL,
    created_by VARCHAR(100) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    submitted_at DATETIME(6) NULL,
    published_at DATETIME(6) NULL,
    row_version BIGINT NOT NULL DEFAULT 1,
    CONSTRAINT fk_program_template_revision_template FOREIGN KEY (template_id) REFERENCES program_template(id),
    CONSTRAINT ck_program_template_revision_state CHECK (state IN ('Draft','PendingReview','PendingApproval','Rejected','Published','Superseded','Archived')),
    UNIQUE KEY ux_program_template_revision_version_attempt (template_id,version_major,version_minor,version_patch,attempt_number),
    KEY ix_program_template_revision_state (state,created_at),
    KEY ix_program_template_revision_creator (created_by,created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

ALTER TABLE program_template
    ADD CONSTRAINT fk_program_template_current_revision FOREIGN KEY (current_published_revision_id) REFERENCES program_template_revision(id);

CREATE TABLE program_template_parameter (
    id BINARY(16) NOT NULL PRIMARY KEY,
    revision_id BINARY(16) NOT NULL,
    direction VARCHAR(20) NOT NULL,
    sort_order INT NOT NULL,
    name VARCHAR(100) NOT NULL,
    data_type VARCHAR(100) NOT NULL,
    default_value VARCHAR(200) NULL,
    unit VARCHAR(50) NULL,
    description VARCHAR(500) NULL,
    CONSTRAINT fk_program_template_parameter_revision FOREIGN KEY (revision_id) REFERENCES program_template_revision(id),
    CONSTRAINT ck_program_template_parameter_direction CHECK (direction IN ('Input','Output','InOut')),
    UNIQUE KEY ux_program_template_parameter_name (revision_id,name),
    KEY ix_program_template_parameter_order (revision_id,direction,sort_order)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE program_template_approval_task (
    id BINARY(16) NOT NULL PRIMARY KEY,
    revision_id BINARY(16) NOT NULL,
    stage VARCHAR(20) NOT NULL,
    assignee VARCHAR(100) NULL,
    assignee_role_code VARCHAR(100) NULL,
    decision VARCHAR(20) NULL,
    decision_by VARCHAR(100) NULL,
    comment VARCHAR(1000) NULL,
    checklist_json JSON NOT NULL,
    created_at DATETIME(6) NOT NULL,
    decided_at DATETIME(6) NULL,
    row_version BIGINT NOT NULL DEFAULT 1,
    CONSTRAINT fk_program_template_task_revision FOREIGN KEY (revision_id) REFERENCES program_template_revision(id),
    CONSTRAINT ck_program_template_task_stage CHECK (stage IN ('Review','Approval')),
    CONSTRAINT ck_program_template_task_decision CHECK (decision IS NULL OR decision IN ('Approved','Rejected')),
    CONSTRAINT ck_program_template_task_assignee CHECK (
        (stage='Review' AND assignee IS NOT NULL AND assignee_role_code IS NULL)
        OR (stage='Approval' AND assignee IS NULL AND assignee_role_code IS NOT NULL)
    ),
    UNIQUE KEY ux_program_template_task_stage (revision_id,stage),
    KEY ix_program_template_task_assignee (assignee,decision,created_at),
    KEY ix_program_template_task_role (assignee_role_code,decision,created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at)
SELECT role_code,'program-template.view',UTC_TIMESTAMP(6) FROM role_definition;

INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at) VALUES
('ElectricalEngineer','program-template.submit',UTC_TIMESTAMP(6)),
('CommissioningEngineer','program-template.submit',UTC_TIMESTAMP(6)),
('HardwareEngineer','program-template.submit',UTC_TIMESTAMP(6)),
('BusinessUnitManager','program-template.review',UTC_TIMESTAMP(6)),
('Approver','program-template.approve',UTC_TIMESTAMP(6)),
('Administrator','program-template.submit',UTC_TIMESTAMP(6)),
('Administrator','program-template.review',UTC_TIMESTAMP(6)),
('Administrator','program-template.approve',UTC_TIMESTAMP(6)),
('Administrator','program-template.manage',UTC_TIMESTAMP(6)),
('developer','program-template.submit',UTC_TIMESTAMP(6)),
('developer','program-template.review',UTC_TIMESTAMP(6)),
('developer','program-template.approve',UTC_TIMESTAMP(6)),
('developer','program-template.manage',UTC_TIMESTAMP(6));
