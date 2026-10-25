-- 仅更正PDM自动生成的电气件BOM表头名称，保留料号、审批及U9C关联。
INSERT INTO audit_entry(id,occurred_at,actor,action_name,entity_type,entity_id,detail_json)
SELECT UUID_TO_BIN(UUID()),UTC_TIMESTAMP(6),'system','material.bom-header-name.normalize','PdmMaterial',BIN_TO_UUID(m.id),
       JSON_OBJECT('oldName',m.name,'newName',CONCAT(p.code,' 电气件BOM'))
FROM material_master m
JOIN project_bom_header h ON h.material_id=m.id AND h.bom_kind='Electrical'
JOIN project p ON p.id=h.project_id
WHERE m.source_system='Pdm' AND m.master_owner='Pdm' AND m.name=CONCAT(p.code,' 电气BOM');

UPDATE material_master m
JOIN project_bom_header h ON h.material_id=m.id AND h.bom_kind='Electrical'
JOIN project p ON p.id=h.project_id
SET m.name=CONCAT(p.code,' 电气件BOM'),m.updated_by='system',m.updated_at=UTC_TIMESTAMP(6),m.row_version=m.row_version+1
WHERE m.source_system='Pdm' AND m.master_owner='Pdm' AND m.name=CONCAT(p.code,' 电气BOM');
