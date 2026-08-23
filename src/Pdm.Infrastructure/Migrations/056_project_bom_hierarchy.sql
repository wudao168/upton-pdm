ALTER TABLE project
    ADD COLUMN root_project_id BINARY(16) NULL AFTER parent_project_id,
    ADD COLUMN bom_item_category_code CHAR(4) NULL AFTER child_sequence;

UPDATE project
SET root_project_id=id,
    bom_item_category_code=COALESCE(bom_item_category_code,'0302')
WHERE parent_project_id IS NULL;

UPDATE project child
INNER JOIN project parent ON parent.id=child.parent_project_id
SET child.root_project_id=COALESCE(parent.root_project_id,parent.id),
    child.bom_item_category_code='0302'
WHERE child.parent_project_id IS NOT NULL;

ALTER TABLE project
    ADD CONSTRAINT fk_project_root FOREIGN KEY (root_project_id) REFERENCES project(id),
    ADD KEY ix_project_root (root_project_id),
    ADD CONSTRAINT ck_project_bom_item_category CHECK (bom_item_category_code IS NULL OR bom_item_category_code IN ('0301','0302'));
