CREATE TABLE material_relation_revision_main (
    revision_id BINARY(16) NOT NULL,
    material_id BINARY(16) NOT NULL,
    PRIMARY KEY (revision_id,material_id),
    KEY ix_material_relation_revision_main_material (material_id),
    CONSTRAINT fk_material_relation_revision_main_revision FOREIGN KEY (revision_id) REFERENCES material_relation_revision(id) ON DELETE CASCADE,
    CONSTRAINT fk_material_relation_revision_main_material FOREIGN KEY (material_id) REFERENCES material_master(id)
);

-- 既有每个版本仍保留原来的单主料，不自动合并配置。
INSERT INTO material_relation_revision_main(revision_id,material_id)
SELECT r.id,t.main_material_id FROM material_relation_revision r JOIN material_relation_template t ON t.id=r.template_id;

-- 旧列作为兼容锚点保留；归属由当前草稿与已发布版本的主料范围决定。
ALTER TABLE material_relation_template ADD KEY ix_material_relation_template_anchor(main_material_id);
ALTER TABLE material_relation_template DROP INDEX ux_material_relation_template_main;
