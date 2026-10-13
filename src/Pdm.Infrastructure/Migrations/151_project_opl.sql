CREATE TABLE project_opl_issue (
    id BINARY(16) NOT NULL PRIMARY KEY,
    project_id BINARY(16) NOT NULL,
    issue_number INT NOT NULL,
    row_version BIGINT NOT NULL,
    payload_json JSON NOT NULL,
    UNIQUE KEY uq_project_opl_number (project_id, issue_number),
    CONSTRAINT fk_project_opl_project FOREIGN KEY (project_id) REFERENCES project(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
