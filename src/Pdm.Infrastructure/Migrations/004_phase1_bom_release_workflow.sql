ALTER TABLE release_package
    ADD COLUMN mechanical_bom_snapshot_json JSON NULL AFTER electrical_bom_revision,
    ADD COLUMN electrical_bom_snapshot_json JSON NULL AFTER mechanical_bom_snapshot_json,
    ADD COLUMN publish_error VARCHAR(2000) NULL AFTER published_path;

UPDATE release_package p
SET mechanical_bom_snapshot_json = COALESCE((
        SELECT JSON_ARRAYAGG(JSON_OBJECT(
            'id', LOWER(CONCAT(
                HEX(SUBSTR(b.id, 1, 4)), '-', HEX(SUBSTR(b.id, 5, 2)), '-', HEX(SUBSTR(b.id, 7, 2)), '-',
                HEX(SUBSTR(b.id, 9, 2)), '-', HEX(SUBSTR(b.id, 11, 6)))),
            'projectId', LOWER(CONCAT(
                HEX(SUBSTR(b.project_id, 1, 4)), '-', HEX(SUBSTR(b.project_id, 5, 2)), '-', HEX(SUBSTR(b.project_id, 7, 2)), '-',
                HEX(SUBSTR(b.project_id, 9, 2)), '-', HEX(SUBSTR(b.project_id, 11, 6)))),
            'kind', 0,
            'sequence', b.sequence_no,
            'drawingNumber', b.drawing_number,
            'name', b.name,
            'quantity', b.quantity,
            'unit', b.unit,
            'material', b.material,
            'specification', b.specification,
            'revision', b.revision_label,
            'isComplete', b.is_complete))
        FROM bom_item b WHERE b.project_id=p.project_id AND b.bom_kind='Mechanical'
    ), JSON_ARRAY()),
    electrical_bom_snapshot_json = COALESCE((
        SELECT JSON_ARRAYAGG(JSON_OBJECT(
            'id', LOWER(CONCAT(
                HEX(SUBSTR(b.id, 1, 4)), '-', HEX(SUBSTR(b.id, 5, 2)), '-', HEX(SUBSTR(b.id, 7, 2)), '-',
                HEX(SUBSTR(b.id, 9, 2)), '-', HEX(SUBSTR(b.id, 11, 6)))),
            'projectId', LOWER(CONCAT(
                HEX(SUBSTR(b.project_id, 1, 4)), '-', HEX(SUBSTR(b.project_id, 5, 2)), '-', HEX(SUBSTR(b.project_id, 7, 2)), '-',
                HEX(SUBSTR(b.project_id, 9, 2)), '-', HEX(SUBSTR(b.project_id, 11, 6)))),
            'kind', 1,
            'sequence', b.sequence_no,
            'drawingNumber', b.drawing_number,
            'name', b.name,
            'quantity', b.quantity,
            'unit', b.unit,
            'material', b.material,
            'specification', b.specification,
            'revision', b.revision_label,
            'isComplete', b.is_complete))
        FROM bom_item b WHERE b.project_id=p.project_id AND b.bom_kind='Electrical'
    ), JSON_ARRAY());

ALTER TABLE release_package
    MODIFY mechanical_bom_snapshot_json JSON NOT NULL,
    MODIFY electrical_bom_snapshot_json JSON NOT NULL;
