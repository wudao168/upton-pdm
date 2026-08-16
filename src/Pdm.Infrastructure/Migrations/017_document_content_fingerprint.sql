ALTER TABLE document
    ADD COLUMN source_fingerprint_sha256 CHAR(64) NULL AFTER file_name,
    ADD KEY ix_document_project_source_fingerprint (project_id, source_fingerprint_sha256);

UPDATE document d
SET d.source_fingerprint_sha256 = (
    SELECT COALESCE(
        NULLIF(JSON_UNQUOTE(JSON_EXTRACT(v.property_snapshot_json, '$.SourceFileSha256')), ''),
        v.sha256)
    FROM document_version v
    WHERE v.document_id = d.id
    ORDER BY v.created_at DESC
    LIMIT 1)
WHERE d.source_fingerprint_sha256 IS NULL;
