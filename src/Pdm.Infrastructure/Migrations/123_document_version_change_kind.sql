-- 版本变更性质：区分"内容变更"与"仅属性写入"（2D图纸审核后的属性回写、插件仅同步属性）。
-- 属性回写会产生新版本，但零件几何没有变化；下游（BOM 对比/发布基线）需要据此把
-- "版本变化"和"真修改"分开，避免整张已发布 BOM 都被判成需要修改。
ALTER TABLE document_version
    ADD COLUMN change_kind VARCHAR(30) NOT NULL DEFAULT 'Content' AFTER version_status;

-- 119 里放开了预览列的补充；这里保持其余列的不可变性，并把新的 change_kind 也纳入不可变集合。
DROP TRIGGER IF EXISTS document_version_immutable_update;
CREATE TRIGGER document_version_immutable_update
BEFORE UPDATE ON document_version
FOR EACH ROW
BEGIN
    IF NOT (NEW.id <=> OLD.id
        AND NEW.document_id <=> OLD.document_id
        AND NEW.revision_label <=> OLD.revision_label
        AND NEW.version_status <=> OLD.version_status
        AND NEW.change_kind <=> OLD.change_kind
        AND NEW.storage_relative_path <=> OLD.storage_relative_path
        AND NEW.file_length <=> OLD.file_length
        AND NEW.sha256 <=> OLD.sha256
        AND NEW.comment <=> OLD.comment
        AND NEW.property_snapshot_json <=> OLD.property_snapshot_json
        AND NEW.reference_snapshot_json <=> OLD.reference_snapshot_json
        AND NEW.mechanical_bom_snapshot_json <=> OLD.mechanical_bom_snapshot_json
        AND NEW.electrical_bom_snapshot_json <=> OLD.electrical_bom_snapshot_json
        AND NEW.source_version_id <=> OLD.source_version_id
        AND NEW.source_description <=> OLD.source_description
        AND NEW.approval_task_id <=> OLD.approval_task_id
        AND NEW.release_package_id <=> OLD.release_package_id
        AND NEW.created_by <=> OLD.created_by
        AND NEW.created_at <=> OLD.created_at)
    THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Document versions are immutable';
    END IF;
END;
