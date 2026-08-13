INSERT IGNORE INTO project_reference_root(project_id, reference_snapshot_id, updated_at)
SELECT candidate.project_id, candidate.id, candidate.captured_at
FROM reference_snapshot candidate
INNER JOIN document root_document
    ON root_document.id = candidate.root_document_id
   AND root_document.project_id = candidate.project_id
WHERE root_document.kind = 'Assembly'
  AND JSON_UNQUOTE(JSON_EXTRACT(candidate.root_json, '$.instancePath')) NOT LIKE '%/%'
  AND NOT EXISTS (
      SELECT 1
      FROM reference_snapshot newer
      INNER JOIN document newer_root
          ON newer_root.id = newer.root_document_id
         AND newer_root.project_id = newer.project_id
      WHERE newer.project_id = candidate.project_id
        AND newer_root.kind = 'Assembly'
        AND JSON_UNQUOTE(JSON_EXTRACT(newer.root_json, '$.instancePath')) NOT LIKE '%/%'
        AND (
            newer.captured_at > candidate.captured_at
            OR (newer.captured_at = candidate.captured_at AND newer.id > candidate.id)
        )
  );
