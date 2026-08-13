CREATE TABLE IF NOT EXISTS project_reference_root (
    project_id BINARY(16) NOT NULL PRIMARY KEY,
    reference_snapshot_id BINARY(16) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    CONSTRAINT fk_project_reference_root_project
        FOREIGN KEY (project_id) REFERENCES project(id),
    CONSTRAINT fk_project_reference_root_snapshot
        FOREIGN KEY (reference_snapshot_id) REFERENCES reference_snapshot(id),
    KEY ix_project_reference_root_snapshot (reference_snapshot_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
