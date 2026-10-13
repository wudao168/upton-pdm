CREATE TABLE IF NOT EXISTS bom_release_confirmation (
    project_id BINARY(16) NOT NULL,
    bom_item_id BINARY(16) NOT NULL,
    confirmation_json JSON NOT NULL,
    PRIMARY KEY (project_id, bom_item_id),
    CONSTRAINT fk_bom_confirmation_project FOREIGN KEY (project_id) REFERENCES project(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
