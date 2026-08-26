CREATE TABLE IF NOT EXISTS pdm_user_role (
    user_id BINARY(16) NOT NULL,
    role_code VARCHAR(100) NOT NULL,
    is_primary TINYINT(1) NOT NULL DEFAULT 0,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (user_id, role_code),
    KEY ix_pdm_user_role_role (role_code, user_id),
    CONSTRAINT fk_pdm_user_role_user FOREIGN KEY (user_id) REFERENCES pdm_user(id) ON DELETE CASCADE,
    CONSTRAINT fk_pdm_user_role_role FOREIGN KEY (role_code) REFERENCES role_definition(role_code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO pdm_user_role(user_id,role_code,is_primary,created_at)
SELECT user_account.id,definition.role_code,1,UTC_TIMESTAMP(6)
FROM pdm_user user_account
INNER JOIN role_definition definition
    ON definition.role_code=COALESCE(NULLIF(user_account.assigned_role_code,''),user_account.role);
