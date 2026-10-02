CREATE TABLE project_nameplate (
    project_id BINARY(16) NOT NULL PRIMARY KEY,
    row_version BIGINT NOT NULL,
    payload_json JSON NOT NULL,
    CONSTRAINT fk_project_nameplate_project FOREIGN KEY (project_id) REFERENCES project(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
