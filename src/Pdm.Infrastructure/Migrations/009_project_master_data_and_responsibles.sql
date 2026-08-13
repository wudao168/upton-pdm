CREATE TABLE IF NOT EXISTS pdm_customer (
    id BINARY(16) NOT NULL PRIMARY KEY,
    code VARCHAR(30) NOT NULL,
    name VARCHAR(200) NOT NULL,
    is_active TINYINT(1) NOT NULL DEFAULT 1,
    row_version BIGINT NOT NULL DEFAULT 1,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY ux_pdm_customer_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO pdm_customer(id,code,name,is_active,row_version,created_at,updated_at)
SELECT UUID_TO_BIN(UUID()), source.customer_code, source.customer_name, 1, 1, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
FROM (
    SELECT customer_code, MAX(customer_name) customer_name
    FROM project
    WHERE customer_code IS NOT NULL AND customer_code <> '' AND customer_name IS NOT NULL AND customer_name <> ''
    GROUP BY customer_code
) source;

CREATE TABLE IF NOT EXISTS project_responsible (
    project_id BINARY(16) NOT NULL,
    username VARCHAR(100) NOT NULL,
    assigned_at DATETIME(6) NOT NULL,
    PRIMARY KEY (project_id,username),
    CONSTRAINT fk_project_responsible_project FOREIGN KEY (project_id) REFERENCES project(id),
    CONSTRAINT fk_project_responsible_user FOREIGN KEY (username) REFERENCES pdm_user(username)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO project_responsible(project_id,username,assigned_at)
SELECT project.id,project.owner,UTC_TIMESTAMP(6)
FROM project
INNER JOIN pdm_user ON pdm_user.username=project.owner
WHERE project.owner IS NOT NULL AND project.owner <> '';

CREATE TABLE IF NOT EXISTS pdm_system_setting (
    setting_key VARCHAR(80) NOT NULL PRIMARY KEY,
    setting_value VARCHAR(1000) NOT NULL,
    updated_at DATETIME(6) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO pdm_system_setting(setting_key,setting_value,updated_at) VALUES
('vault_root','D:\\PDM\\Vault',UTC_TIMESTAMP(6)),
('release_root','D:\\PDM\\Release',UTC_TIMESTAMP(6));
