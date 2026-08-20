ALTER TABLE material_master
    ADD COLUMN u9_sync_confirmed TINYINT(1) NOT NULL DEFAULT 0 AFTER u9_item_code;

UPDATE material_master AS material
JOIN u9_material_sync_task AS task ON task.material_id=material.id
SET material.u9_sync_confirmed=1
WHERE task.status='Succeeded'
  AND (
      JSON_UNQUOTE(JSON_EXTRACT(task.response_preview,'$.created'))='true'
      OR JSON_UNQUOTE(JSON_EXTRACT(task.response_preview,'$.updated'))='true'
  );
