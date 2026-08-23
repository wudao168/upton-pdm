ALTER TABLE bom_item
    ADD COLUMN source_instance_path VARCHAR(1000) NULL AFTER source_configuration,
    ADD COLUMN parent_drawing_number VARCHAR(160) NULL AFTER source_instance_path;

CREATE INDEX ix_bom_item_source_occurrence
    ON bom_item(project_id, source_document_id, source_instance_path(191));
