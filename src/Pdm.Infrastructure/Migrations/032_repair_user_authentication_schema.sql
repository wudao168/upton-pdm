SET @pdm_schema_name = DATABASE();

SET @pdm_sql = IF(
    EXISTS(
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = @pdm_schema_name AND table_name = 'pdm_user' AND column_name = 'nickname'
    ),
    'SELECT 1',
    'ALTER TABLE pdm_user ADD COLUMN nickname VARCHAR(80) NULL AFTER display_name'
);
PREPARE pdm_stmt FROM @pdm_sql;
EXECUTE pdm_stmt;
DEALLOCATE PREPARE pdm_stmt;

SET @pdm_sql = IF(
    EXISTS(
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = @pdm_schema_name AND table_name = 'pdm_user' AND column_name = 'gender'
    ),
    'SELECT 1',
    'ALTER TABLE pdm_user ADD COLUMN gender VARCHAR(20) NOT NULL DEFAULT ''unspecified'' AFTER nickname'
);
PREPARE pdm_stmt FROM @pdm_sql;
EXECUTE pdm_stmt;
DEALLOCATE PREPARE pdm_stmt;

SET @pdm_sql = IF(
    EXISTS(
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = @pdm_schema_name AND table_name = 'pdm_user' AND column_name = 'landline'
    ),
    'SELECT 1',
    'ALTER TABLE pdm_user ADD COLUMN landline VARCHAR(40) NULL AFTER gender'
);
PREPARE pdm_stmt FROM @pdm_sql;
EXECUTE pdm_stmt;
DEALLOCATE PREPARE pdm_stmt;

SET @pdm_sql = IF(
    EXISTS(
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = @pdm_schema_name AND table_name = 'pdm_user' AND column_name = 'mobile_phone'
    ),
    'SELECT 1',
    'ALTER TABLE pdm_user ADD COLUMN mobile_phone VARCHAR(40) NULL AFTER landline'
);
PREPARE pdm_stmt FROM @pdm_sql;
EXECUTE pdm_stmt;
DEALLOCATE PREPARE pdm_stmt;

SET @pdm_sql = IF(
    EXISTS(
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = @pdm_schema_name AND table_name = 'pdm_user' AND column_name = 'email'
    ),
    'SELECT 1',
    'ALTER TABLE pdm_user ADD COLUMN email VARCHAR(120) NULL AFTER mobile_phone'
);
PREPARE pdm_stmt FROM @pdm_sql;
EXECUTE pdm_stmt;
DEALLOCATE PREPARE pdm_stmt;

SET @pdm_sql = IF(
    EXISTS(
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = @pdm_schema_name AND table_name = 'pdm_user' AND column_name = 'token_version'
    ),
    'SELECT 1',
    'ALTER TABLE pdm_user ADD COLUMN token_version BIGINT NOT NULL DEFAULT 0 AFTER email'
);
PREPARE pdm_stmt FROM @pdm_sql;
EXECUTE pdm_stmt;
DEALLOCATE PREPARE pdm_stmt;

CREATE TABLE IF NOT EXISTS password_reset_request (
    id BINARY(16) NOT NULL PRIMARY KEY,
    user_id BINARY(16) NOT NULL,
    requester_username VARCHAR(100) NOT NULL,
    requester_display_name VARCHAR(100) NOT NULL,
    active_username VARCHAR(100) NULL,
    requested_at DATETIME(6) NOT NULL,
    completed_at DATETIME(6) NULL,
    completed_by VARCHAR(100) NULL,
    CONSTRAINT fk_password_reset_request_user FOREIGN KEY (user_id) REFERENCES pdm_user(id),
    UNIQUE KEY ux_password_reset_request_active_username (active_username),
    KEY ix_password_reset_request_pending (completed_at, requested_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
