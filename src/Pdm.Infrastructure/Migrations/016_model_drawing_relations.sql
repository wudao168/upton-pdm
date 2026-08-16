CREATE TABLE IF NOT EXISTS document_model_drawing_relation (
    drawing_document_id BINARY(16) NOT NULL PRIMARY KEY,
    model_document_id BINARY(16) NOT NULL,
    project_id BINARY(16) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    KEY ix_document_model_drawing_model (model_document_id),
    KEY ix_document_model_drawing_project (project_id),
    CONSTRAINT fk_document_model_drawing_drawing FOREIGN KEY (drawing_document_id) REFERENCES document(id) ON DELETE CASCADE,
    CONSTRAINT fk_document_model_drawing_model FOREIGN KEY (model_document_id) REFERENCES document(id) ON DELETE CASCADE,
    CONSTRAINT fk_document_model_drawing_project FOREIGN KEY (project_id) REFERENCES project(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO document_model_drawing_relation(
    drawing_document_id, model_document_id, project_id, created_at, updated_at)
SELECT drawing.id, model.id, drawing.project_id, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
FROM document drawing
INNER JOIN document model
    ON model.project_id = drawing.project_id
   AND model.drawing_number = drawing.drawing_number
   AND model.kind IN ('Assembly', 'Part')
WHERE drawing.kind = 'Drawing'
  AND NOT EXISTS (
      SELECT 1
      FROM document earlier_model
      WHERE earlier_model.project_id = model.project_id
        AND earlier_model.drawing_number = model.drawing_number
        AND earlier_model.kind IN ('Assembly', 'Part')
        AND earlier_model.id < model.id
  );
