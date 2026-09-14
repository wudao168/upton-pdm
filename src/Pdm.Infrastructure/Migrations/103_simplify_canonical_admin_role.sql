DELETE assignment
FROM pdm_user_role assignment
INNER JOIN pdm_user user_account ON user_account.id=assignment.user_id
WHERE user_account.username='admin'
  AND assignment.role_code<>'Administrator';

UPDATE pdm_user_role assignment
INNER JOIN pdm_user user_account ON user_account.id=assignment.user_id
SET assignment.is_primary=1
WHERE user_account.username='admin'
  AND assignment.role_code='Administrator';

UPDATE pdm_user
SET role='Administrator',
    assigned_role_code='Administrator',
    cross_company_view=1,
    token_version=token_version+1,
    row_version=row_version+1
WHERE username='admin';
