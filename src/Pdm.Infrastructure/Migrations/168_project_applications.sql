ALTER TABLE project
    ADD COLUMN is_special_initiation TINYINT(1) NOT NULL DEFAULT 0,
    ADD COLUMN source_application_id BINARY(16) NULL,
    ADD COLUMN source_application_number VARCHAR(40) NULL,
    ADD COLUMN delivery_date DATE NULL,
    ADD UNIQUE KEY uq_project_source_application (source_application_id);

CREATE TABLE project_application (
    id BINARY(16) NOT NULL PRIMARY KEY,
    company_id BINARY(16) NOT NULL,
    number VARCHAR(40) NOT NULL,
    applicant VARCHAR(100) NOT NULL,
    status VARCHAR(30) NOT NULL,
    row_version BIGINT NOT NULL,
    payload_json JSON NOT NULL,
    UNIQUE KEY uq_project_application_number (number),
    KEY ix_project_application_company (company_id, applicant, status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE project_application_history (
    id BINARY(16) NOT NULL PRIMARY KEY,
    application_id BINARY(16) NOT NULL,
    row_version BIGINT NOT NULL,
    payload_json JSON NOT NULL,
    UNIQUE KEY uq_project_application_history_version (application_id, row_version),
    CONSTRAINT fk_project_application_history FOREIGN KEY (application_id) REFERENCES project_application(id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO role_permission(role_code,permission_code,updated_at) VALUES
('SalesAssistant','project.application.submit',UTC_TIMESTAMP(6)),
('PlanningManager','project.application.approve',UTC_TIMESTAMP(6)),
('Administrator','project.application.submit',UTC_TIMESTAMP(6)),
('Administrator','project.application.approve',UTC_TIMESTAMP(6)),
('developer','project.application.submit',UTC_TIMESTAMP(6)),
('developer','project.application.approve',UTC_TIMESTAMP(6));
