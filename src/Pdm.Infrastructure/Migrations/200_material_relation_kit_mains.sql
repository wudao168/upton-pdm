-- A relation main may reference a material or a kit without creating a material-master copy.
CREATE TABLE material_relation_main_source (
    id BINARY(16) NOT NULL PRIMARY KEY,
    material_id BINARY(16) NULL,
    kit_id BINARY(16) NULL,
    CONSTRAINT fk_relation_main_source_material FOREIGN KEY (material_id) REFERENCES material_master(id) ON DELETE CASCADE,
    CONSTRAINT fk_relation_main_source_kit FOREIGN KEY (kit_id) REFERENCES engineering_kit(id) ON DELETE CASCADE,
    CONSTRAINT ck_relation_main_source_identity CHECK (
        (material_id IS NOT NULL AND material_id=id AND kit_id IS NULL)
        OR (kit_id IS NOT NULL AND kit_id=id AND material_id IS NULL))
);
INSERT INTO material_relation_main_source(id,material_id)
SELECT main_material_id,main_material_id FROM material_relation_template
UNION SELECT material_id,material_id FROM material_relation_revision_main;
ALTER TABLE material_relation_template DROP FOREIGN KEY fk_material_relation_template_main;
ALTER TABLE material_relation_template ADD CONSTRAINT fk_material_relation_template_source
    FOREIGN KEY (main_material_id) REFERENCES material_relation_main_source(id);
ALTER TABLE material_relation_revision_main DROP FOREIGN KEY fk_material_relation_revision_main_material;
ALTER TABLE material_relation_revision_main ADD CONSTRAINT fk_material_relation_revision_main_source
    FOREIGN KEY (material_id) REFERENCES material_relation_main_source(id);

-- Keep material-only writers compatible with the new foreign-key target.
CREATE TRIGGER material_relation_template_register_source BEFORE INSERT ON material_relation_template FOR EACH ROW
    INSERT IGNORE INTO material_relation_main_source(id,material_id)
    SELECT id,id FROM material_master WHERE id=NEW.main_material_id;
CREATE TRIGGER material_relation_revision_register_source BEFORE INSERT ON material_relation_revision_main FOR EACH ROW
    INSERT IGNORE INTO material_relation_main_source(id,material_id)
    SELECT id,id FROM material_master WHERE id=NEW.material_id;
