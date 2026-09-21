-- 转图与发布解耦后，发布时没拿到预览的正式版本需要在后台补挂 STEP/PDF。
-- document_version 的不可变触发器（002_immutable_document_versions.sql）会拦掉这次 UPDATE，
-- 导致后台转图成功也无法落库（报“Document versions are immutable”）。这里保留其他列的不可变性，
-- 只放开预览列的补充。
DROP TRIGGER IF EXISTS document_version_immutable_update;
CREATE TRIGGER document_version_immutable_update
BEFORE UPDATE ON document_version
FOR EACH ROW
BEGIN
    IF NOT (NEW.id <=> OLD.id
        AND NEW.document_id <=> OLD.document_id
        AND NEW.revision_label <=> OLD.revision_label
        AND NEW.version_status <=> OLD.version_status
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
