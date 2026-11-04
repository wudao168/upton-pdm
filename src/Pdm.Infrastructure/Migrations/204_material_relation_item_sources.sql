-- 保留配件与主料的来源，清除选配或移除配件后仍能准确核对本次发布影响的主料。
CREATE TABLE IF NOT EXISTS material_relation_item_source (
    project_id BINARY(16) NOT NULL,
    accessory_bom_item_id BINARY(16) NOT NULL,
    main_bom_item_id BINARY(16) NOT NULL,
    PRIMARY KEY(project_id,accessory_bom_item_id,main_bom_item_id),
    CONSTRAINT fk_relation_item_source_project FOREIGN KEY(project_id) REFERENCES project(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO material_relation_item_source(project_id,accessory_bom_item_id,main_bom_item_id)
SELECT selection.project_id,selection.accessory_bom_item_id,selection.main_bom_item_id
FROM material_relation_selection selection INNER JOIN project ON project.id=selection.project_id;
