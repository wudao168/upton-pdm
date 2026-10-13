CREATE TABLE IF NOT EXISTS project_reference_roots (
    project_id BINARY(16) NOT NULL,
    root_document_id BINARY(16) NOT NULL,
    reference_snapshot_id BINARY(16) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (project_id, root_document_id),
    CONSTRAINT fk_reference_roots_project FOREIGN KEY (project_id) REFERENCES project(id),
    CONSTRAINT fk_reference_roots_document FOREIGN KEY (root_document_id) REFERENCES document(id),
    CONSTRAINT fk_reference_roots_snapshot FOREIGN KEY (reference_snapshot_id) REFERENCES reference_snapshot(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO project_reference_roots(project_id, root_document_id, reference_snapshot_id, updated_at)
SELECT current_root.project_id, snapshot.root_document_id, snapshot.id, current_root.updated_at
FROM project_reference_root current_root
JOIN reference_snapshot snapshot ON snapshot.id=current_root.reference_snapshot_id;
