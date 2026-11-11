CREATE TABLE IF NOT EXISTS project_expense_request (
    id BINARY(16) NOT NULL PRIMARY KEY,
    project_id BINARY(16) NOT NULL,
    row_version BIGINT NOT NULL,
    payload_json JSON NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    INDEX ix_expense_project (project_id, updated_at),
    CONSTRAINT fk_expense_project FOREIGN KEY (project_id) REFERENCES project(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
CREATE TABLE IF NOT EXISTS project_expense_request_version (
    request_id BINARY(16) NOT NULL,
    row_version BIGINT NOT NULL,
    payload_json JSON NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (request_id, row_version),
    CONSTRAINT fk_expense_version FOREIGN KEY (request_id) REFERENCES project_expense_request(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
