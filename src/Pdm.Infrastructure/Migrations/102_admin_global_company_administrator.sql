UPDATE pdm_user
SET role='Administrator',
    assigned_role_code='Administrator',
    cross_company_view=1,
    token_version=token_version+1,
    row_version=row_version+1
WHERE username='admin'
  AND (role<>'Administrator' OR assigned_role_code<>'Administrator' OR cross_company_view<>1);

UPDATE pdm_user_role assignment
INNER JOIN pdm_user user_account ON user_account.id=assignment.user_id
SET assignment.is_primary=0
WHERE user_account.username='admin';

INSERT INTO pdm_user_role(user_id,role_code,is_primary,created_at)
SELECT id,'Administrator',1,UTC_TIMESTAMP(6)
FROM pdm_user
WHERE username='admin'
ON DUPLICATE KEY UPDATE is_primary=1;
