CREATE TABLE IF NOT EXISTS project_bonus_settings (
    company_id BINARY(16) NOT NULL PRIMARY KEY,
    row_version BIGINT NOT NULL DEFAULT 0,
    payload_json JSON NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
CREATE TABLE IF NOT EXISTS project_bonus_book (
    project_id BINARY(16) NOT NULL PRIMARY KEY,
    row_version BIGINT NOT NULL,
    payload_json JSON NOT NULL,
    CONSTRAINT fk_bonus_project FOREIGN KEY (project_id) REFERENCES project(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
CREATE TABLE IF NOT EXISTS project_bonus_history (
    project_id BINARY(16) NOT NULL,
    row_version BIGINT NOT NULL,
    payload_json JSON NOT NULL,
    PRIMARY KEY(project_id,row_version),
    CONSTRAINT fk_bonus_history FOREIGN KEY (project_id) REFERENCES project_bonus_book(project_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
CREATE TABLE IF NOT EXISTS project_bonus_settings_history (
    company_id BINARY(16) NOT NULL,
    row_version BIGINT NOT NULL,
    payload_json JSON NOT NULL,
    PRIMARY KEY(company_id,row_version)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
