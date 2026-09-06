CREATE TABLE material_relation_review (
    project_id BINARY(16) NOT NULL,
    main_bom_item_id BINARY(16) NOT NULL,
    revision_id BINARY(16) NOT NULL,
    group_id BINARY(16) NOT NULL,
    decision VARCHAR(30) NOT NULL,
    main_quantity DECIMAL(18,4) NOT NULL,
    reason VARCHAR(500) NULL,
    updated_by VARCHAR(100) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY(project_id,main_bom_item_id,group_id),
    KEY ix_material_relation_review_main (project_id,main_bom_item_id),
    CONSTRAINT fk_material_relation_review_project FOREIGN KEY (project_id) REFERENCES project(id) ON DELETE CASCADE,
    CONSTRAINT fk_material_relation_review_revision FOREIGN KEY (revision_id) REFERENCES material_relation_revision(id),
    CONSTRAINT fk_material_relation_review_group FOREIGN KEY (group_id) REFERENCES material_relation_group(id)
);
