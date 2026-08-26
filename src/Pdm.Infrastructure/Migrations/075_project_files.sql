CREATE TABLE IF NOT EXISTS project_file (
    id BINARY(16) NOT NULL PRIMARY KEY,
    root_project_id BINARY(16) NOT NULL,
    folder_id BINARY(16) NOT NULL,
    file_name VARCHAR(255) NOT NULL,
    created_by VARCHAR(100) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    updated_by VARCHAR(100) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    deleted_at DATETIME(6) NULL,
    deleted_by VARCHAR(100) NULL,
    active_file_name VARCHAR(255) GENERATED ALWAYS AS (CASE WHEN deleted_at IS NULL THEN LOWER(file_name) ELSE NULL END) STORED,
    UNIQUE KEY ux_project_file_active_name (folder_id,active_file_name),
    KEY ix_project_file_root (root_project_id),
    KEY ix_project_file_deleted (deleted_at),
    CONSTRAINT fk_project_file_root FOREIGN KEY (root_project_id) REFERENCES project(id) ON DELETE CASCADE,
    CONSTRAINT fk_project_file_folder FOREIGN KEY (folder_id) REFERENCES project_folder(id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS project_file_version (
    id BINARY(16) NOT NULL PRIMARY KEY,
    project_file_id BINARY(16) NOT NULL,
    version_number INT NOT NULL,
    file_name VARCHAR(255) NOT NULL,
    storage_root VARCHAR(1000) NOT NULL,
    storage_relative_path VARCHAR(1500) NOT NULL,
    file_length BIGINT NOT NULL,
    sha256 CHAR(64) NOT NULL,
    uploaded_by VARCHAR(100) NOT NULL,
    uploaded_at DATETIME(6) NOT NULL,
    comment VARCHAR(500) NULL,
    UNIQUE KEY ux_project_file_version_number (project_file_id,version_number),
    UNIQUE KEY ux_project_file_version_path (storage_relative_path(500)),
    CONSTRAINT fk_project_file_version_file FOREIGN KEY (project_file_id) REFERENCES project_file(id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

DROP TRIGGER IF EXISTS trg_project_file_version_no_update;
CREATE TRIGGER trg_project_file_version_no_update
BEFORE UPDATE ON project_file_version
FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='project file versions are immutable';

DROP TRIGGER IF EXISTS trg_project_file_version_no_delete;
CREATE TRIGGER trg_project_file_version_no_delete
BEFORE DELETE ON project_file_version
FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='project file versions are immutable';

SET @folder_name_unique_exists = (
    SELECT COUNT(*) FROM information_schema.statistics
    WHERE table_schema=DATABASE() AND table_name='project_folder' AND index_name='ux_project_folder_sibling_name'
);
SET @folder_name_unique_sql = IF(@folder_name_unique_exists=0,
    'CREATE UNIQUE INDEX ux_project_folder_sibling_name ON project_folder(parent_folder_id,name)',
    'SELECT 1');
PREPARE folder_name_unique_stmt FROM @folder_name_unique_sql;
EXECUTE folder_name_unique_stmt;
DEALLOCATE PREPARE folder_name_unique_stmt;
