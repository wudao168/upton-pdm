-- 旧子项目清单并入主项目，保留主项目编号；子项目问题按原顺序续编。
CREATE TEMPORARY TABLE opl_family_merge AS
SELECT issue.id, issue.project_id AS old_project_id, issue.row_version+1 AS new_version,
       COALESCE(project.root_project_id, project.parent_project_id) AS root_id,
       COALESCE(root_max.max_number, 0) + ROW_NUMBER() OVER (
           PARTITION BY COALESCE(project.root_project_id, project.parent_project_id)
           ORDER BY project.code, issue.issue_number, issue.id) AS new_number
FROM project_opl_issue issue
JOIN project ON project.id=issue.project_id
LEFT JOIN (SELECT project_id, MAX(issue_number) AS max_number FROM project_opl_issue GROUP BY project_id) root_max
    ON root_max.project_id=COALESCE(project.root_project_id, project.parent_project_id)
WHERE project.parent_project_id IS NOT NULL;

UPDATE project_opl_issue issue
JOIN opl_family_merge merge_row ON merge_row.id=issue.id
SET issue.project_id=merge_row.root_id,
    issue.issue_number=merge_row.new_number,
    issue.row_version=merge_row.new_version,
    issue.payload_json=JSON_SET(issue.payload_json,
        '$.relatedProjectId', JSON_UNQUOTE(JSON_EXTRACT(issue.payload_json, '$.projectId')),
        '$.attachmentProjectId', JSON_UNQUOTE(JSON_EXTRACT(issue.payload_json, '$.projectId')),
        '$.projectId', LOWER(BIN_TO_UUID(merge_row.root_id)),
        '$.number', merge_row.new_number,
        '$.rowVersion', merge_row.new_version);

DROP TEMPORARY TABLE opl_family_merge;
