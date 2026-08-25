ALTER TABLE material_code_application
    DROP FOREIGN KEY fk_material_application_bom_item;

ALTER TABLE material_code_application
    MODIFY COLUMN bom_item_id BINARY(16) NULL,
    ADD COLUMN bom_header_kind VARCHAR(30) NULL AFTER bom_item_id;

ALTER TABLE material_code_application
    ADD CONSTRAINT fk_material_application_bom_item
        FOREIGN KEY (bom_item_id) REFERENCES bom_item(id) ON DELETE CASCADE,
    ADD KEY ix_material_application_header_status (project_id,bom_header_kind,status,requested_at);
