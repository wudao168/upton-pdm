ALTER TABLE document_model_drawing_relation
    DROP PRIMARY KEY,
    ADD PRIMARY KEY (drawing_document_id, model_document_id);
