ALTER TABLE pdm_user
    ADD COLUMN nickname VARCHAR(80) NULL AFTER display_name,
    ADD COLUMN gender VARCHAR(20) NOT NULL DEFAULT 'unspecified' AFTER nickname,
    ADD COLUMN landline VARCHAR(40) NULL AFTER gender,
    ADD COLUMN mobile_phone VARCHAR(40) NULL AFTER landline,
    ADD COLUMN email VARCHAR(120) NULL AFTER mobile_phone,
    ADD COLUMN token_version BIGINT NOT NULL DEFAULT 0 AFTER email;

CREATE TABLE password_reset_request (
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
