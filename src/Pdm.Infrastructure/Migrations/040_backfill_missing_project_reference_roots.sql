INSERT IGNORE INTO project_reference_root(project_id, reference_snapshot_id, updated_at)
SELECT candidate.project_id, candidate.id, candidate.captured_at
FROM reference_snapshot candidate
INNER JOIN document root_document
    ON root_document.id = candidate.root_document_id
   AND root_document.project_id = candidate.project_id
WHERE root_document.kind = 'Assembly'
  AND NOT EXISTS (
      SELECT 1
      FROM reference_snapshot container
      WHERE container.project_id = candidate.project_id
        AND container.root_document_id <> candidate.root_document_id
        AND JSON_SEARCH(
            JSON_EXTRACT(container.root_json, '$.children'),
            'one',
            root_document.file_name
        ) IS NOT NULL
  )
  AND NOT EXISTS (
      SELECT 1
      FROM reference_snapshot newer
      INNER JOIN document newer_root
          ON newer_root.id = newer.root_document_id
         AND newer_root.project_id = newer.project_id
      WHERE newer.project_id = candidate.project_id
        AND newer_root.kind = 'Assembly'
        AND NOT EXISTS (
            SELECT 1
            FROM reference_snapshot newer_container
            WHERE newer_container.project_id = newer.project_id
              AND newer_container.root_document_id <> newer.root_document_id
              AND JSON_SEARCH(
                  JSON_EXTRACT(newer_container.root_json, '$.children'),
                  'one',
                  newer_root.file_name
              ) IS NOT NULL
        )
        AND (
            newer.captured_at > candidate.captured_at
            OR (newer.captured_at = candidate.captured_at AND newer.id > candidate.id)
        )
  );
