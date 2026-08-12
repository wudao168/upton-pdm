CREATE TABLE IF NOT EXISTS project_user_access (
    project_id BINARY(16) NOT NULL,
    username VARCHAR(100) NOT NULL,
    can_read TINYINT(1) NOT NULL DEFAULT 1,
    granted_at DATETIME(6) NOT NULL,
    PRIMARY KEY (project_id, username),
    CONSTRAINT fk_project_user_access_project FOREIGN KEY (project_id) REFERENCES project(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS document_user_access (
    document_id BINARY(16) NOT NULL,
    username VARCHAR(100) NOT NULL,
    can_read TINYINT(1) NOT NULL,
    granted_at DATETIME(6) NOT NULL,
    PRIMARY KEY (document_id, username),
    CONSTRAINT fk_document_user_access_document FOREIGN KEY (document_id) REFERENCES document(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO project_user_access(project_id, username, can_read, granted_at)
SELECT p.id, u.username, 1, UTC_TIMESTAMP(6)
FROM project p
CROSS JOIN pdm_user u
WHERE p.is_active = 1 AND u.is_active = 1;
